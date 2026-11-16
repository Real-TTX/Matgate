# A second server for the tests in NlaServerTests.cs, one that shares no code with FreeRDP: the CredSSP
# acceptor and the NTLM implementation of pyspnego (Python, written from the same specifications by somebody
# else), behind just enough RDP to get there - the X.224 negotiation and TLS on the connection.
#
# pyspnego's acceptor is used as it is, with two changes that have nothing to do with the exchange itself:
#   - it normally carries TLS inside its tokens (that is how WinRM does CredSSP); here TLS is on the socket,
#     so the three methods that deal with TLS are replaced by ones that pass the bytes through;
#   - the version it announces is a constant of its module, which this script sets per connection so that
#     the old way of binding the public key (versions 2 to 4) can be tested as well.
#
# A client chooses the variant with the cookie of its connection request, "Cookie: mstshash=<variant>":
#   v2 v3 v4 v5 v6   the CredSSP version the server announces (v6 without a cookie)
#   v6ex             version 6 and PROTOCOL_HYBRID_EX: the server sends the Early User Authorization Result
#
# After a completed exchange the server sends 32 bytes: SHA-256 over "domain\nuser\npassword" (UTF-8) of the
# credentials it was handed, so that a test can tell that they arrived as they were sent. Then it waits for
# the client to close the connection.

import hashlib
import os
import socket
import ssl
import struct
import sys
import tempfile
import threading
import traceback

import spnego._credssp as credssp
from spnego._context import UnwrapResult, WrapResult
from spnego.tls import CredSSPTLSContext, generate_tls_certificate

PROTOCOL_HYBRID = 0x2
PROTOCOL_HYBRID_EX = 0x8

# The announced version is module state in pyspnego, so one exchange at a time.
exchange = threading.Lock()


class Acceptor(credssp.CredSSPProxy):
    """pyspnego's CredSSP acceptor without its own TLS layer."""

    def _step_tls(self, in_token):
        return in_token
        yield  # never reached; it makes this a generator like the method it replaces

    def wrap(self, data, encrypt=True, qop=None):
        return WrapResult(data=data, encrypted=True)

    def unwrap(self, data):
        return UnwrapResult(data=data, encrypted=True, qop=0)


def read_exactly(connection, count):
    data = b""
    while len(data) < count:
        chunk = connection.recv(count - len(data))
        if not chunk:
            raise EOFError("the client closed the connection")
        data += chunk
    return data


def read_ts_request(connection):
    """One DER structure: tag, length (short or long form), content."""
    head = read_exactly(connection, 2)
    length = head[1]
    if length & 0x80:
        extra = read_exactly(connection, length & 0x7F)
        head += extra
        length = int.from_bytes(extra, "big")
    return head + read_exactly(connection, length)


def serve(raw, tls_context, public_key):
    # X.224 Connection Request: TPKT header, seven bytes of X.224, an optional cookie line, the negotiation request.
    tpkt = read_exactly(raw, 4)
    request = read_exactly(raw, struct.unpack(">H", tpkt[2:4])[0] - 4)
    variant = "v6"
    cookie = request[7:-8]
    if cookie.startswith(b"Cookie: mstshash="):
        variant = cookie[len(b"Cookie: mstshash="):].strip().decode()

    selected = PROTOCOL_HYBRID_EX if variant.endswith("ex") else PROTOCOL_HYBRID
    version = int(variant.removeprefix("v").removesuffix("ex"))

    # X.224 Connection Confirm with the negotiation response.
    raw.sendall(bytes.fromhex("030000130ed00000123400" + "02000800") + struct.pack("<I", selected))
    with tls_context.wrap_socket(raw, server_side=True) as connection:
        with exchange:
            credssp._CREDSSP_VERSION = version
            acceptor = Acceptor(
                usage="accept",
                protocol="credssp",
                credssp_tls_context=CredSSPTLSContext(context=tls_context, public_key=public_key),
            )
            while not acceptor.complete:
                answer = acceptor.step(read_ts_request(connection))
                if answer:
                    connection.sendall(answer)
                elif not acceptor.complete:
                    # Versions 2 and 5 have no way to say why: the acceptor just stops, and so does the connection.
                    print(f"{variant}: refused without a message", flush=True)
                    return

        credential = acceptor.get_extra_info("client_credential")
        print(f"{variant}: accepted {credential.domain_name!r} {credential.username!r}", flush=True)
        if selected == PROTOCOL_HYBRID_EX:
            connection.sendall(struct.pack("<I", 0))

        received = "\n".join([credential.domain_name, credential.username, credential.password])
        connection.sendall(hashlib.sha256(received.encode()).digest())
        connection.settimeout(30)
        connection.recv(1)


def main():
    certificate, key, public_key = generate_tls_certificate()
    tls_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    with tempfile.TemporaryDirectory() as directory:
        certificate_file = os.path.join(directory, "server.crt")
        key_file = os.path.join(directory, "server.key")
        with open(certificate_file, "wb") as file:
            file.write(certificate)
        with open(key_file, "wb") as file:
            file.write(key)
        tls_context.load_cert_chain(certificate_file, key_file)

    listener = socket.create_server(("0.0.0.0", int(os.environ.get("PORT", "3389"))))
    print("listening", flush=True)
    while True:
        raw, _ = listener.accept()

        def run(raw=raw):
            try:
                raw.settimeout(30)
                serve(raw, tls_context, public_key)
            except Exception:  # whatever goes wrong with one client must not end the server
                print(traceback.format_exc().strip().splitlines()[-1], flush=True)
            finally:
                raw.close()

        threading.Thread(target=run, daemon=True).start()


if __name__ == "__main__":
    sys.exit(main())
