using GuraFile.Storage;

namespace GuraFile.Tests;

[TestClass]
public sealed class FileKeyboardShortcutRouterTests
{
    [TestMethod]
    public void Evaluate_WhenTextInputFocused_ReturnsNone()
    {
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.C, isControlPressed: true, isShiftPressed: false, isTextInputFocused: true));
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.X, isControlPressed: true, isShiftPressed: false, isTextInputFocused: true));
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.V, isControlPressed: true, isShiftPressed: false, isTextInputFocused: true));
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.A, isControlPressed: true, isShiftPressed: false, isTextInputFocused: true));
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.Delete, isControlPressed: false, isShiftPressed: false, isTextInputFocused: true));
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.F2, isControlPressed: false, isShiftPressed: false, isTextInputFocused: true));
    }

    [TestMethod]
    public void Evaluate_ShiftDelete_IsExplicitlyIgnored()
    {
        Assert.AreEqual(
            FileShortcutCommand.None,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.Delete, isControlPressed: false, isShiftPressed: true, isTextInputFocused: false));
    }

    [TestMethod]
    public void Evaluate_CtrlShortcuts_ResolveCorrectly()
    {
        Assert.AreEqual(
            FileShortcutCommand.Copy,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.C, isControlPressed: true, isShiftPressed: false, isTextInputFocused: false));
        Assert.AreEqual(
            FileShortcutCommand.Cut,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.X, isControlPressed: true, isShiftPressed: false, isTextInputFocused: false));
        Assert.AreEqual(
            FileShortcutCommand.Paste,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.V, isControlPressed: true, isShiftPressed: false, isTextInputFocused: false));
        Assert.AreEqual(
            FileShortcutCommand.SelectAll,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.A, isControlPressed: true, isShiftPressed: false, isTextInputFocused: false));
    }

    [TestMethod]
    public void Evaluate_SingleKeyShortcuts_ResolveCorrectly()
    {
        Assert.AreEqual(
            FileShortcutCommand.Rename,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.F2, isControlPressed: false, isShiftPressed: false, isTextInputFocused: false));
        Assert.AreEqual(
            FileShortcutCommand.Delete,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.Delete, isControlPressed: false, isShiftPressed: false, isTextInputFocused: false));
        Assert.AreEqual(
            FileShortcutCommand.Refresh,
            FileKeyboardShortcutRouter.Evaluate(FileShortcutKey.F5, isControlPressed: false, isShiftPressed: false, isTextInputFocused: false));
    }
}
