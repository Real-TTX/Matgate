using System.Text;
using System.Text.Json;

namespace Matgate.Models.Tests;

public class KeyBarTests
{
    private static KeyBarButton Button(params string[] keys) => new() { Id = "b", Keys = [.. keys] };

    private static string Hex(string text) => Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text));

    // ---- what may be stored ----------------------------------------------------------------------------------

    [Fact]
    public void ModifiersComeFirstAndOnceWhateverTheOrder()
    {
        Assert.Equal(["Ctrl", "Alt", "Delete"], KeyBar.Normalize(["delete", "ALT", "ctrl", "Alt"]));
        Assert.Equal(["Ctrl", "Shift", "Z"], KeyBar.Normalize(["Shift", "z", "Ctrl"]));
    }

    [Fact]
    public void ALetterIsStoredAsItIsPrintedOnTheKey()
    {
        Assert.Equal(["C"], KeyBar.Normalize(["c"]));
        Assert.Equal(["Ä"], KeyBar.Normalize(["ä"]));
        Assert.Equal(["|"], KeyBar.Normalize(["|"]));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("F13")]
    [InlineData("\t")]
    [InlineData("\u0007")]
    public void WhatIsNoKeyIsRefused(string key)
    {
        Assert.Null(KeyBar.Normalize([key]));
    }

    [Fact]
    public void MoreThanOneKeyOrNoneIsRefused()
    {
        Assert.Null(KeyBar.Normalize(["A", "B"]));
        Assert.Null(KeyBar.Normalize(["Tab", "Esc"]));
        Assert.Null(KeyBar.Normalize([]));
        Assert.Null(KeyBar.Normalize(["", ""]));
    }

    [Fact]
    public void OnlyModifiersCanWaitForTheNextKey()
    {
        Assert.True(KeyBar.Clean(new KeyBarButton { Keys = ["Ctrl"], Sticky = true })!.Sticky);
        Assert.True(KeyBar.Clean(new KeyBarButton { Keys = ["Ctrl", "Shift"], Sticky = true })!.Sticky);
        Assert.False(KeyBar.Clean(new KeyBarButton { Keys = ["Ctrl", "C"], Sticky = true })!.Sticky);
    }

    [Fact]
    public void AButtonFromAFileIsCleanedUp()
    {
        var clean = KeyBar.Clean(new KeyBarButton { Id = "Not An Id", Label = "  a label that is far too long  ", Keys = ["x", "ctrl"], Scope = "moon" })!;
        Assert.True(KeyBar.IsId(clean.Id));
        Assert.True(clean.Label.Length <= KeyBar.MaxLabelLength);
        Assert.Equal(["Ctrl", "X"], clean.Keys);
        Assert.Equal(KeyBar.ScopeAll, clean.Scope);
        Assert.Null(KeyBar.Clean(new KeyBarButton { Keys = ["nonsense"] }));
    }

    [Fact]
    public void AListKeepsItsOrderDropsWhatIsNoButtonAndHasNoIdTwice()
    {
        var list = KeyBar.Clean(
        [
            new KeyBarButton { Id = "one", Keys = ["Esc"] },
            new KeyBarButton { Id = "bad", Keys = ["A", "B"] },
            new KeyBarButton { Id = "one", Keys = ["Tab"] },
        ]);
        Assert.Equal(2, list.Count);
        Assert.Equal(["Esc"], list[0].Keys);
        Assert.Equal(["Tab"], list[1].Keys);
        Assert.NotEqual(list[0].Id, list[1].Id);
        Assert.True(KeyBar.Clean(Enumerable.Range(0, 100).Select(_ => new KeyBarButton { Keys = ["A"] })).Count <= KeyBar.MaxButtons);
    }

    [Fact]
    public void WithoutAListOfTheirOwnAUserHasTheDefaultAndAnEmptyListStaysEmpty()
    {
        Assert.Equal(KeyBar.Defaults().Select(button => button.Id), KeyBar.For(new MatgateUser()).Select(button => button.Id));
        Assert.Empty(KeyBar.For(new MatgateUser { KeyBar = [] }));
        var own = KeyBar.For(new MatgateUser { KeyBar = [new KeyBarButton { Id = "mine", Keys = ["F5"] }] });
        Assert.Equal(["mine"], own.Select(button => button.Id));
    }

    [Fact]
    public void EveryDefaultButtonIsAButtonAndHasItsOwnId()
    {
        var defaults = KeyBar.Defaults();
        Assert.Equal(defaults.Count, KeyBar.Clean(defaults).Count);
        Assert.Equal(defaults.Count, defaults.Select(button => button.Id).Distinct().Count());
        Assert.All(defaults, button => Assert.True(KeyBar.IsId(button.Id)));
        Assert.All(defaults, button => Assert.Equal(button.Keys, KeyBar.Normalize(button.Keys)));
    }

    // ---- what is written on a button -------------------------------------------------------------------------

    [Fact]
    public void TheLabelComesFromTheKeysInTheUsersLanguage()
    {
        Assert.Equal("Strg+C", KeyBar.Label(Button("Ctrl", "C"), de: true));
        Assert.Equal("Ctrl+C", KeyBar.Label(Button("Ctrl", "C"), de: false));
        Assert.Equal("Strg+Alt+Entf", KeyBar.Label(Button("Ctrl", "Alt", "Delete"), de: true));
        Assert.Equal("⇧Tab", KeyBar.Label(Button("Shift", "Tab"), de: true));
        Assert.Equal("Strg+⇧Z", KeyBar.Label(Button("Ctrl", "Shift", "Z"), de: true));
        Assert.Equal("Strg", KeyBar.Label(Button("Ctrl"), de: true));
        Assert.Equal("⇧", KeyBar.Label(Button("Shift"), de: true));
        Assert.Equal("←", KeyBar.Label(Button("Left"), de: true));
        Assert.Equal("Mein", KeyBar.Label(new KeyBarButton { Label = "Mein", Keys = ["F5"] }, de: true));
    }

    [Fact]
    public void TheDescriptionNamesTheKeysInWords()
    {
        Assert.Equal("Strg + Alt + Entf", KeyBar.Describe(Button("Ctrl", "Alt", "Delete"), de: true));
        Assert.Equal("Shift + Tab", KeyBar.Describe(Button("Shift", "Tab"), de: false));
        Assert.Equal("Windows + R", KeyBar.Describe(Button("Win", "R"), de: false));
    }

    // ---- the screen page: scan codes, keysyms, text ----------------------------------------------------------

    [Fact]
    public void ANamedKeyIsAScanCodeWithItsKeysym()
    {
        var left = KeyBar.ForScreen(Button("Left"), de: true)!;
        Assert.Equal(new KeyBar.ScanKey(0x4B, true, 0xFF51), left.Key);
        Assert.Empty(left.Mods);
        Assert.Null(left.Text);
        var f11 = KeyBar.ForScreen(Button("F11"), de: true)!;
        Assert.Equal(new KeyBar.ScanKey(0x57, false, 0xFFC8), f11.Key);
        var windows = KeyBar.ForScreen(Button("Win"), de: true)!;
        Assert.Equal([new KeyBar.ScanKey(0x5B, true, 0xFFEB)], windows.Mods);
        Assert.Null(windows.Key);
    }

    [Fact]
    public void ACharacterTravelsAsTextSoThatTheMachinesLayoutDecidesWhereItSits()
    {
        var copy = KeyBar.ForScreen(Button("Ctrl", "C"), de: true)!;
        Assert.Equal([new KeyBar.ScanKey(0x1D, false, 0xFFE3)], copy.Mods);
        Assert.Null(copy.Key);
        Assert.Equal("c", copy.Text);
        // With Shift among other modifiers the character is the upper-case one, and Shift is held.
        var redo = KeyBar.ForScreen(Button("Ctrl", "Shift", "Z"), de: true)!;
        Assert.Equal("Z", redo.Text);
        Assert.Equal(2, redo.Mods.Count);
        // Shift alone needs no key held: the character says it.
        var upper = KeyBar.ForScreen(Button("Shift", "A"), de: true)!;
        Assert.Equal("A", upper.Text);
        Assert.Empty(upper.Mods);
        Assert.Equal("a", KeyBar.ForScreen(Button("A"), de: true)!.Text);
    }

    [Fact]
    public void TwoShortcutsHaveABetterWayThanTheirKeys()
    {
        Assert.Equal(KeyBar.ActionSecureAttention, KeyBar.ForScreen(Button("Ctrl", "Alt", "Delete"), de: true)!.Action);
        Assert.Equal(KeyBar.ActionPaste, KeyBar.ForScreen(Button("Ctrl", "V"), de: true)!.Action);
        Assert.Null(KeyBar.ForScreen(Button("Ctrl", "C"), de: true)!.Action);
        Assert.Null(KeyBar.ForScreen(Button("Ctrl", "Shift", "V"), de: true)!.Action);
    }

    [Fact]
    public void AButtonShowsWhereItsScopeSays()
    {
        var terminalOnly = new KeyBarButton { Id = "t", Keys = ["Enter"], Scope = KeyBar.ScopeTerminal };
        var screenOnly = new KeyBarButton { Id = "s", Keys = ["Delete"], Scope = KeyBar.ScopeScreen };
        Assert.Null(KeyBar.ForScreen(terminalOnly, de: true));
        Assert.NotNull(KeyBar.ForTerminal(terminalOnly, de: true));
        Assert.NotNull(KeyBar.ForScreen(screenOnly, de: true));
        Assert.Null(KeyBar.ForTerminal(screenOnly, de: true));
        Assert.Equal(KeyBar.ScopeScreen, KeyBar.ForGuacamole(screenOnly, de: true).Scope);
    }

    // ---- the terminal page: bytes ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Esc", 0, "1b")]
    [InlineData("Tab", 0, "09")]
    [InlineData("Tab", KeyBar.MaskShift, "1b5b5a")]
    [InlineData("Enter", 0, "0d")]
    [InlineData("Backspace", 0, "7f")]
    [InlineData("Backspace", KeyBar.MaskCtrl, "08")]
    [InlineData("Space", KeyBar.MaskCtrl, "00")]
    [InlineData("Up", 0, "1b5b41")]
    [InlineData("Left", 0, "1b5b44")]
    [InlineData("Home", 0, "1b5b48")]
    [InlineData("End", 0, "1b5b46")]
    [InlineData("Delete", 0, "1b5b337e")]
    [InlineData("PageDown", 0, "1b5b367e")]
    [InlineData("F1", 0, "1b4f50")]
    [InlineData("F4", 0, "1b4f53")]
    [InlineData("F5", 0, "1b5b31357e")]
    [InlineData("F12", 0, "1b5b32347e")]
    public void ANamedKeySendsWhatXtermSends(string key, int mask, string hex)
    {
        Assert.Equal(hex, Convert.ToHexStringLower(KeyBar.TerminalBytes(key, mask)!));
    }

    [Fact]
    public void ModifiersOfACursorOrFunctionKeyGoIntoItsSequence()
    {
        // Ctrl+Left: word to the left. Alt+Up, Shift+F5, Ctrl+Shift+End.
        Assert.Equal(Hex("\u001B[1;5D"), Convert.ToHexStringLower(KeyBar.TerminalBytes("Left", KeyBar.MaskCtrl)!));
        Assert.Equal(Hex("\u001B[1;3A"), Convert.ToHexStringLower(KeyBar.TerminalBytes("Up", KeyBar.MaskAlt)!));
        Assert.Equal(Hex("\u001B[15;2~"), Convert.ToHexStringLower(KeyBar.TerminalBytes("F5", KeyBar.MaskShift)!));
        Assert.Equal(Hex("\u001B[1;6F"), Convert.ToHexStringLower(KeyBar.TerminalBytes("End", KeyBar.MaskCtrl | KeyBar.MaskShift)!));
        Assert.Equal(Hex("\u001B[1;5P"), Convert.ToHexStringLower(KeyBar.TerminalBytes("F1", KeyBar.MaskCtrl)!));
        Assert.Equal(Hex("\u001B[3;5~"), Convert.ToHexStringLower(KeyBar.TerminalBytes("Delete", KeyBar.MaskCtrl)!));
    }

    [Theory]
    [InlineData("C", KeyBar.MaskCtrl, "03")]
    [InlineData("D", KeyBar.MaskCtrl, "04")]
    [InlineData("Z", KeyBar.MaskCtrl, "1a")]
    [InlineData("[", KeyBar.MaskCtrl, "1b")]
    [InlineData("\\", KeyBar.MaskCtrl, "1c")]
    [InlineData("_", KeyBar.MaskCtrl, "1f")]
    [InlineData("?", KeyBar.MaskCtrl, "7f")]
    [InlineData("C", KeyBar.MaskCtrl | KeyBar.MaskShift, "03")]
    [InlineData("B", KeyBar.MaskAlt, "1b62")]
    [InlineData("B", KeyBar.MaskAlt | KeyBar.MaskShift, "1b42")]
    [InlineData("C", KeyBar.MaskCtrl | KeyBar.MaskAlt, "1b03")]
    [InlineData("A", 0, "61")]
    [InlineData("A", KeyBar.MaskShift, "41")]
    [InlineData("|", 0, "7c")]
    [InlineData("Ä", 0, "c3a4")]
    public void ACharacterWithCtrlIsItsControlCharacterAndWithAltHasAnEscapeInFront(string key, int mask, string hex)
    {
        Assert.Equal(hex, Convert.ToHexStringLower(KeyBar.TerminalBytes(key, mask)!));
    }

    [Fact]
    public void ATerminalButtonCarriesItsKeyForEveryModifierAStickyButtonMayHold()
    {
        var up = KeyBar.ForTerminal(Button("Up"), de: true)!;
        Assert.Equal(8, up.Send!.Count);
        Assert.Equal("1b5b41", up.Send[0]);
        Assert.Equal(Hex("\u001B[1;5A"), up.Send[KeyBar.MaskCtrl]);
        // While a program has the cursor keys in application mode.
        Assert.Equal("1b4f41", up.App);
        // A button's own modifiers are part of every entry.
        var copy = KeyBar.ForTerminal(Button("Ctrl", "C"), de: true)!;
        Assert.Equal(KeyBar.MaskCtrl, copy.Mods);
        Assert.Equal("03", copy.Send![0]);
        Assert.Equal("1b03", copy.Send[KeyBar.MaskAlt]);
        Assert.Null(copy.App);
        Assert.Null(KeyBar.ForTerminal(Button("Ctrl", "Up"), de: true)!.App);
    }

    [Fact]
    public void AStickyModifierHasNoBytesOnlyItsMaskAndATappedOneIsNoTerminalButton()
    {
        var ctrl = KeyBar.ForTerminal(new KeyBarButton { Id = "c", Keys = ["Ctrl"], Sticky = true }, de: true)!;
        Assert.True(ctrl.Sticky);
        Assert.Equal(KeyBar.MaskCtrl, ctrl.Mods);
        Assert.Null(ctrl.Send);
        Assert.Null(KeyBar.ForTerminal(Button("Ctrl"), de: true));
    }

    [Fact]
    public void ATerminalKnowsNoWindowsKeyAndNoKeyWithoutASequence()
    {
        Assert.Null(KeyBar.ForTerminal(Button("Win"), de: true));
        Assert.Null(KeyBar.ForTerminal(Button("Win", "R"), de: true));
        Assert.Null(KeyBar.ForTerminal(Button("PrintScreen"), de: true));
        Assert.Null(KeyBar.ForTerminal(Button("Menu"), de: true));
        Assert.Equal(KeyBar.ActionPaste, KeyBar.ForTerminal(Button("Ctrl", "V"), de: true)!.Action);
        Assert.Null(KeyBar.ForTerminal(Button("Ctrl", "Alt", "Delete"), de: true)!.Action);
    }

    // ---- Guacamole sessions: keysyms -------------------------------------------------------------------------

    [Fact]
    public void AGuacamoleButtonIsItsKeysymsInTheOrderTheyArePressed()
    {
        Assert.Equal([0xFFE3u, 0xFFE9u, 0xFFFFu], KeyBar.ForGuacamole(Button("Ctrl", "Alt", "Delete"), de: true).Syms);
        Assert.Equal([0xFFE3u, 0x63u], KeyBar.ForGuacamole(Button("Ctrl", "C"), de: true).Syms);
        Assert.Equal([0xFFE1u, 0x41u], KeyBar.ForGuacamole(Button("Shift", "A"), de: true).Syms);
        Assert.Equal([0xFFEBu], KeyBar.ForGuacamole(Button("Win"), de: true).Syms);
        // Beyond Latin-1 a character is a "Unicode keysym".
        Assert.Equal([0x01000000u + 0x20AC], KeyBar.ForGuacamole(Button("€"), de: true).Syms);
        var sticky = KeyBar.ForGuacamole(new KeyBarButton { Id = "s", Keys = ["Ctrl", "Shift"], Sticky = true }, de: true);
        Assert.Equal(["ctrl", "shift"], sticky.Mods);
        Assert.True(sticky.Sticky);
    }

    // ---- what the pages get ----------------------------------------------------------------------------------

    [Fact]
    public void ThePagesGetTheBarAsJsonWithTheNamesTheirScriptsRead()
    {
        var user = new MatgateUser { KeyBar = [new KeyBarButton { Id = "copy", Keys = ["Ctrl", "C"] }, new KeyBarButton { Id = "hold", Keys = ["Alt"], Sticky = true }] };
        var screen = JsonDocument.Parse(KeyBar.ScreenJson(user, de: true)).RootElement;
        Assert.Equal("copy", screen[0].GetProperty("id").GetString());
        Assert.Equal("Strg+C", screen[0].GetProperty("label").GetString());
        Assert.Equal(0x1D, screen[0].GetProperty("mods")[0].GetProperty("s").GetInt32());
        Assert.False(screen[0].GetProperty("mods")[0].GetProperty("e").GetBoolean());
        Assert.Equal(0xFFE3, screen[0].GetProperty("mods")[0].GetProperty("ks").GetInt32());
        Assert.Equal("c", screen[0].GetProperty("text").GetString());
        Assert.True(screen[1].GetProperty("sticky").GetBoolean());

        var terminal = JsonDocument.Parse(KeyBar.TerminalJson(user, de: false)).RootElement;
        Assert.Equal("03", terminal[0].GetProperty("send")[0].GetString());
        Assert.Equal(KeyBar.MaskAlt, terminal[1].GetProperty("mods").GetInt32());

        var guacamole = JsonDocument.Parse(KeyBar.GuacamoleJson(user, de: false)).RootElement;
        Assert.Equal(0x63, guacamole[0].GetProperty("syms")[1].GetInt32());
        Assert.Equal("all", guacamole[0].GetProperty("scope").GetString());
        Assert.Equal("alt", guacamole[1].GetProperty("mods")[0].GetString());
    }

    [Fact]
    public void TheDefaultBarHasWhatTheTwoBarsBeforeItHad()
    {
        var user = new MatgateUser();
        var screen = KeyBar.ForScreen(KeyBar.For(user), de: true).Select(button => button.Label).ToList();
        Assert.Equal(["Esc", "Tab", "Strg", "Alt", "⊞", "Entf", "←", "↑", "↓", "→", "Strg+C", "Strg+V", "Strg+Alt+Entf"], screen);
        var terminal = KeyBar.ForTerminal(KeyBar.For(user), de: true).Select(button => button.Label).ToList();
        Assert.Equal(["Esc", "Tab", "⇧Tab", "Strg", "Alt", "←", "↑", "↓", "→", "Strg+C", "Strg+V", "⏎"], terminal);
    }
}
