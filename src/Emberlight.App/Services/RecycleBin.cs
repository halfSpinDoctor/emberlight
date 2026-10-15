using Emberlight.Core;
using Microsoft.VisualBasic.FileIO;

namespace Emberlight.App.Services;

/// <summary>Moves files to the Windows Recycle Bin (never deletes permanently).</summary>
public sealed class RecycleBin : IRecycleBin
{
    public static readonly RecycleBin Instance = new();

    private RecycleBin() { }

    public bool TryDelete(string path, out string? error)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
