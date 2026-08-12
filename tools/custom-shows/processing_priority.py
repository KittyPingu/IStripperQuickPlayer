"""Apply QuickPlayer's Windows CPU/GPU priority to the current worker process."""
import ctypes
import os


_CPU = {
    "idle": 0x00000040,
    "below-normal": 0x00004000,
    "normal": 0x00000020,
    "above-normal": 0x00008000,
    "high": 0x00000080,
}
_GPU = {
    "idle": 0,
    "below-normal": 1,
    "normal": 2,
    "above-normal": 3,
    "high": 4,
}


def apply_processing_priorities():
    if os.name != "nt":
        return
    kernel32 = ctypes.windll.kernel32
    gdi32 = ctypes.windll.gdi32
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.SetPriorityClass.argtypes = (ctypes.c_void_p, ctypes.c_uint)
    kernel32.SetPriorityClass.restype = ctypes.c_bool
    gdi32.D3DKMTSetProcessSchedulingPriorityClass.argtypes = (
        ctypes.c_void_p, ctypes.c_int)
    gdi32.D3DKMTSetProcessSchedulingPriorityClass.restype = ctypes.c_long
    handle = kernel32.GetCurrentProcess()
    cpu = os.environ.get("IQP_CPU_PRIORITY", "normal").lower()
    gpu = os.environ.get("IQP_GPU_PRIORITY", "normal").lower()
    try:
        kernel32.SetPriorityClass(handle, _CPU.get(cpu, _CPU["normal"]))
    except (AttributeError, OSError):
        pass
    try:
        gdi32.D3DKMTSetProcessSchedulingPriorityClass(
            handle, _GPU.get(gpu, _GPU["normal"]))
    except (AttributeError, OSError):
        pass
