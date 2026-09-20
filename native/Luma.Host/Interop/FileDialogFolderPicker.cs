using System.Runtime.InteropServices;
using Luma.Host.Bridge;

namespace Luma.Host.Interop;

/// <summary>
/// 现代系统文件夹选择对话框（IFileOpenDialog + FOS_PICKFOLDERS），Windows 11 风格；
/// 模态归属设置窗口，取消返回 null。必须在 STA UI 线程调用。
/// </summary>
public sealed class FileDialogFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(IntPtr owner)
    {
        var path = Pick(owner);
        return Task.FromResult(path);
    }

    private static string? Pick(IntPtr owner)
    {
        try
        {
            var clsid = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
            var type = Type.GetTypeFromCLSID(clsid)
                       ?? throw new InvalidOperationException("系统未提供 IFileOpenDialog 组件");
            var dialog = (IFileOpenDialog)(Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("文件夹选择对话框创建失败"));
            try
            {
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle("选择项目文件夹");
                var hr = dialog.Show(owner);
                if (hr == HRESULT_CANCEL) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out var item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
                    try
                    {
                        var path = pathPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(pathPtr);
                        return string.IsNullOrWhiteSpace(path) ? null : path;
                    }
                    finally
                    {
                        if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"文件夹选择对话框失败: {ex.Message}");
            return null;
        }
    }

    private const uint FOS_PICKFOLDERS = 0x0000_0020;
    private const uint FOS_FORCEFILESYSTEM = 0x0000_0010;
    private const uint FOS_PATHMUSTEXIST = 0x0000_0800;
    private const int HRESULT_CANCEL = unchecked((int)0x8000_4004); // ERROR_CANCELLED
    private const uint SIGDN_FILESYSPATH = 0x8005_8000;

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr hwndOwner);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName(IntPtr pszName);
        void GetFileName(out IntPtr pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel(IntPtr pszText);
        void SetFileNameLabel(IntPtr pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension(IntPtr pszDefaultExtension);
        void Close();
        void SetClientGuid(IntPtr guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
