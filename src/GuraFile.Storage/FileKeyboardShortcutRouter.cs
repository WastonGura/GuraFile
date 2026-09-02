namespace GuraFile.Storage;

public enum FileShortcutKey
{
    None = 0,
    C = 67,
    X = 88,
    V = 86,
    A = 65,
    F2 = 113,
    F5 = 116,
    Delete = 46
}

public enum FileShortcutCommand
{
    None = 0,
    Copy,
    Cut,
    Paste,
    Rename,
    Delete,
    SelectAll,
    Refresh
}

public static class FileKeyboardShortcutRouter
{
    public static FileShortcutCommand Evaluate(
        FileShortcutKey key,
        bool isControlPressed,
        bool isShiftPressed,
        bool isTextInputFocused)
    {
        if (isTextInputFocused)
        {
            return FileShortcutCommand.None;
        }

        // Shift+Delete is strictly blocked (no permanent deletion allowed)
        if (isShiftPressed && key == FileShortcutKey.Delete)
        {
            return FileShortcutCommand.None;
        }

        if (isControlPressed && !isShiftPressed)
        {
            return key switch
            {
                FileShortcutKey.C => FileShortcutCommand.Copy,
                FileShortcutKey.X => FileShortcutCommand.Cut,
                FileShortcutKey.V => FileShortcutCommand.Paste,
                FileShortcutKey.A => FileShortcutCommand.SelectAll,
                _ => FileShortcutCommand.None
            };
        }

        if (!isControlPressed && !isShiftPressed)
        {
            return key switch
            {
                FileShortcutKey.F2 => FileShortcutCommand.Rename,
                FileShortcutKey.Delete => FileShortcutCommand.Delete,
                FileShortcutKey.F5 => FileShortcutCommand.Refresh,
                _ => FileShortcutCommand.None
            };
        }

        return FileShortcutCommand.None;
    }
}
