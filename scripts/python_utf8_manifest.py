"""Enable Windows UTF-8 ACP only for the bundled Python, not for the user's OS."""
import ctypes
from pathlib import Path


def enable_utf8(executable: Path):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LoadLibraryExW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint]
    kernel.LoadLibraryExW.restype = ctypes.c_void_p
    kernel.FindResourceW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    kernel.FindResourceW.restype = ctypes.c_void_p
    kernel.LoadResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel.LoadResource.restype = ctypes.c_void_p
    kernel.LockResource.argtypes = [ctypes.c_void_p]
    kernel.LockResource.restype = ctypes.c_void_p
    kernel.SizeofResource.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel.SizeofResource.restype = ctypes.c_uint
    kernel.FreeLibrary.argtypes = [ctypes.c_void_p]
    handle = kernel.LoadLibraryExW(str(executable), None, 2)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        resource = kernel.FindResourceW(handle, 1, 24)
        if not resource:
            raise RuntimeError("Python manifest missing")
        loaded = kernel.LoadResource(handle, resource)
        data = ctypes.string_at(kernel.LockResource(loaded), kernel.SizeofResource(handle, resource))
    finally:
        kernel.FreeLibrary(handle)
    manifest = data.decode("utf-8")
    if "activeCodePage" in manifest:
        if ">UTF-8</activeCodePage>" not in manifest:
            raise RuntimeError("Unexpected Python code page")
        return
    if "</windowsSettings>" not in manifest:
        raise RuntimeError("Unexpected Python manifest")
    manifest = manifest.replace("</windowsSettings>",
        '<activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage></windowsSettings>')
    data = manifest.encode("utf-8")
    kernel.BeginUpdateResourceW.argtypes = [ctypes.c_wchar_p, ctypes.c_bool]
    kernel.BeginUpdateResourceW.restype = ctypes.c_void_p
    kernel.UpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                      ctypes.c_ushort, ctypes.c_void_p, ctypes.c_uint]
    kernel.EndUpdateResourceW.argtypes = [ctypes.c_void_p, ctypes.c_bool]
    update = kernel.BeginUpdateResourceW(str(executable), False)
    if not update:
        raise ctypes.WinError(ctypes.get_last_error())
    if not kernel.UpdateResourceW(update, 24, 1, 0, data, len(data)):
        error = ctypes.get_last_error()
        kernel.EndUpdateResourceW(update, True)
        raise ctypes.WinError(error)
    if not kernel.EndUpdateResourceW(update, False):
        raise ctypes.WinError(ctypes.get_last_error())
