using System;
using CodeWalker.GameFiles;

namespace App.Core;

/// <summary>
/// Drop-in replacement for <see cref="RpfFile.CreateFile"/> that runs
/// <see cref="RpfWriteGuard"/> first, so a write that would corrupt the archive or
/// the game is refused with a clear message instead of silently going through.
/// Every archive write in the app/CLI should go through here.
/// </summary>
public static class RpfSafeWrite
{
    public static RpfFileEntry CreateFile(RpfDirectoryEntry dir, string name, byte[] data, bool overwrite = true)
    {
        if (RpfWriteGuard.Check(dir?.File, name, data) is string err)
            throw new Exception(err);
        return RpfFile.CreateFile(dir, name, data, overwrite);
    }
}
