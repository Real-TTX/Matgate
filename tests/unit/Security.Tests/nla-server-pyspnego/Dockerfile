# A second NLA server for the tests in NlaServerTests.cs, independent of FreeRDP: pyspnego's CredSSP acceptor
# and NTLM implementation behind a minimal RDP front (see server.py). Unlike FreeRDP's server it can announce
# the old CredSSP versions 2 to 5, sends a proper status or - for versions 2 and 5 - just hangs up when it
# refuses a logon, and tells the test what credentials it was handed.
#
#   docker build -t matgate-nla-pyspnego tests/unit/Security.Tests/nla-server-pyspnego
#   docker run -d --name matgate-nla-pyspnego -p 127.0.0.1:33891:3389 matgate-nla-pyspnego
#   MATGATE_NLA_TEST_SECOND_HOST=127.0.0.1:33891 dotnet test tests/unit/Security.Tests
#
# The accounts are the ones of nla-server/Dockerfile, as lines "domain:user:password". This server takes the
# domain seriously: an account answers only to the domain it is listed with.
FROM python:3.12-slim

RUN pip install --no-cache-dir --root-user-action=ignore pyspnego==0.12.4

RUN printf '%s\n' \
      ':tester:Matgate-NLA-2026' \
      'NLATEST:tester:Matgate-NLA-2026' \
      ':tester@nla.test:Matgate-NLA-2026' \
      ':jürgen:Grüße-€-2026' \
      > /etc/ntlm-users

ENV NTLM_USER_FILE=/etc/ntlm-users PYTHONUNBUFFERED=1

COPY server.py /server.py

EXPOSE 3389

CMD ["python", "/server.py"]
