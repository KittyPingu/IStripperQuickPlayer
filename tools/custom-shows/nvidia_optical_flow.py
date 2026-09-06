"""Windows NVOFA binding: hardware motion estimation through the CUDA driver API.

Uses the published Optical Flow SDK 2.0 ABI (supported by newer drivers), with
separate forward/backward evaluations and 8-bit cost output. No CUDA toolkit,
DLSS runtime, CUDA-enabled OpenCV build or compiled Python extension is needed.
ABI reference: NVIDIA/NVIDIAOpticalFlowSDK@edb50da3cf849840d680249aa6dbef248ebce2ca
"""
# SDK interface declarations adapted from NVIDIA's BSD-3-Clause headers:
# Copyright(c) 2020, NVIDIA CORPORATION. All rights reserved.
# Redistribution and use in source and binary forms, with or without
# modification, are permitted provided that the following conditions are met:
# 1. Redistributions of source code must retain the above copyright notice,
#    this list of conditions and the following disclaimer.
# 2. Redistributions in binary form must reproduce the above copyright notice,
#    this list of conditions and the following disclaimer in the documentation
#    and/or other materials provided with the distribution.
# 3. Neither the name of the copyright holder nor the names of its contributors
#    may be used to endorse or promote products derived from this software
#    without specific prior written permission.
# THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
# AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
# IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
# ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
# LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
# CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
# SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
# INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
# CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
# ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
# POSSIBILITY OF SUCH DAMAGE.

import ctypes as c
from contextlib import contextmanager
import sys

U32, PTR = c.c_uint32, c.c_void_p
API_VERSION = 0x20
_libraries = None


class InitParams(c.Structure):
    _fields_ = [(name, U32) for name in ("width", "height", "outGridSize",
        "hintGridSize", "mode", "perfLevel", "enableExternalHints", "enableOutputCost")] + \
        [("hPrivData", PTR), ("disparityRange", U32), ("enableRoi", U32)]


class BufferDescriptor(c.Structure):
    _fields_ = [(name, U32) for name in ("width", "height", "usage", "format")]


class ExecuteInput(c.Structure):
    _fields_ = [("inputFrame", PTR), ("referenceFrame", PTR), ("externalHints", PTR),
        ("disableTemporalHints", U32), ("padding", U32), ("hPrivData", PTR),
        ("padding2", U32), ("numRois", U32), ("roiData", PTR)]


class ExecuteOutput(c.Structure):
    _fields_ = [("outputBuffer", PTR), ("outputCostBuffer", PTR), ("hPrivData", PTR)]


class StrideInfo(c.Structure):
    _fields_ = [("planes", (U32 * 2) * 3), ("numPlanes", U32)]


class CudaCopy2D(c.Structure):
    _fields_ = [("srcX", c.c_size_t), ("srcY", c.c_size_t), ("srcType", U32),
        ("srcHost", PTR), ("srcDevice", c.c_uint64), ("srcArray", PTR),
        ("srcPitch", c.c_size_t), ("dstX", c.c_size_t), ("dstY", c.c_size_t),
        ("dstType", U32), ("dstHost", PTR), ("dstDevice", c.c_uint64),
        ("dstArray", PTR), ("dstPitch", c.c_size_t),
        ("widthBytes", c.c_size_t), ("height", c.c_size_t)]


def bind(library, name, arguments, result=c.c_int):
    function = getattr(library, name)
    function.argtypes, function.restype = arguments, result
    return function


def check(status, operation):
    if status:
        raise RuntimeError(f"NVIDIA hardware optical flow: {operation} failed (status {status})")


class NvidiaOpticalFlow:
    """One single-threaded, explicitly closed NVOFA session on a CUDA device."""

    def __init__(self, width, height, device=0):
        global _libraries
        if sys.platform != "win32" or c.sizeof(PTR) != 8:
            raise RuntimeError("NVIDIA hardware optical flow requires 64-bit Windows")
        self.context, self.handle, self.buffers = PTR(), PTR(), []
        self.width, self.height = width, height
        try:
            if _libraries is None:
                # Search System32 only; retain driver modules until process exit.
                _libraries = (c.WinDLL("nvcuda.dll", winmode=0x800),
                              c.WinDLL("nvofapi64.dll", winmode=0x800))
            cuda, nvof = _libraries
            self.push = bind(cuda, "cuCtxPushCurrent_v2", [PTR])
            self.pop = bind(cuda, "cuCtxPopCurrent_v2", [c.POINTER(PTR)])
            self.destroy_context = bind(cuda, "cuCtxDestroy_v2", [PTR])
            self.synchronize = bind(cuda, "cuCtxSynchronize", [])
            self.copy = bind(cuda, "cuMemcpy2D_v2", [c.POINTER(CudaCopy2D)])
            check(bind(cuda, "cuInit", [U32])(0), "CUDA initialization")
            cuda_device = c.c_int()
            check(bind(cuda, "cuDeviceGet", [c.POINTER(c.c_int), c.c_int])(
                c.byref(cuda_device), device), "CUDA device selection")
            name = c.create_string_buffer(256)
            check(bind(cuda, "cuDeviceGetName", [PTR, c.c_int, c.c_int])(
                name, len(name), cuda_device), "CUDA device name")
            self.device_name = name.value.decode(errors="replace")
            check(bind(cuda, "cuCtxCreate_v2", [c.POINTER(PTR), U32, c.c_int])(
                c.byref(self.context), 0, cuda_device), "CUDA context creation")
            restored = PTR()
            check(self.pop(c.byref(restored)), "restoring CUDA context")
            table = (PTR * 12)()
            check(bind(nvof, "NvOFAPICreateInstanceCuda", [U32, PTR])(
                API_VERSION, table), "NVOFA API initialization")
            signatures = (
                ("create", [PTR, c.POINTER(PTR)], c.c_int),
                ("init", [PTR, c.POINTER(InitParams)], c.c_int),
                ("create_buffer", [PTR, c.POINTER(BufferDescriptor), U32, c.POINTER(PTR)], c.c_int),
                ("get_array", [PTR], PTR),
                ("get_pointer", [PTR], c.c_uint64),
                ("get_stride", [PTR, c.POINTER(StrideInfo)], c.c_int),
                ("set_streams", [PTR, PTR, PTR], c.c_int),
                ("execute", [PTR, c.POINTER(ExecuteInput), c.POINTER(ExecuteOutput)], c.c_int),
                ("destroy_buffer", [PTR], c.c_int),
                ("destroy", [PTR], c.c_int),
                ("last_error", [PTR, PTR, c.POINTER(U32)], c.c_int),
                ("get_caps", [PTR, U32, c.POINTER(U32), c.POINTER(U32)], c.c_int))
            for pointer, (name, arguments, result) in zip(table, signatures):
                if not pointer:
                    raise RuntimeError(f"NVOFA API entry {name} is unavailable")
                setattr(self, name, c.WINFUNCTYPE(result, *arguments)(pointer))
            with self.active():
                check(self.create(self.context, c.byref(self.handle)), "NVOFA session creation")
                self.grid = min(self.caps(0))
                if not (self.caps(4)[0] <= width <= self.caps(6)[0] and
                        self.caps(5)[0] <= height <= self.caps(7)[0]):
                    raise RuntimeError(f"NVOFA does not support {width}x{height} input")
                params = InitParams(width=width, height=height, outGridSize=self.grid,
                    mode=1, perfLevel=5, enableOutputCost=1)
                check(self.init(self.handle, c.byref(params)), "NVOFA session initialization")
                self.flow_width = (width + self.grid - 1) // self.grid
                self.flow_height = (height + self.grid - 1) // self.grid
                self.inputs = [self.allocate(width, height, 1, 1) for _ in range(2)]
                self.flow = self.allocate(self.flow_width, self.flow_height, 2, 5)
                self.cost = self.allocate(self.flow_width, self.flow_height, 4, 7)
        except Exception as error:
            self.close()
            raise RuntimeError("NVIDIA hardware optical flow is unavailable. "
                "Use a supported NVIDIA GPU/driver, choose another detector, or disable temporal cleanup. "
                f"Details: {error}") from error

    @contextmanager
    def active(self):
        check(self.push(self.context), "activating CUDA context")
        try:
            yield
        finally:
            restored = PTR()
            check(self.pop(c.byref(restored)), "restoring CUDA context")

    def caps(self, capability):
        count = U32()
        check(self.get_caps(self.handle, capability, None, c.byref(count)), "querying NVOFA capabilities")
        if not count.value:
            raise RuntimeError(f"NVOFA capability {capability} is unavailable")
        values = (U32 * count.value)()
        check(self.get_caps(self.handle, capability, values, c.byref(count)), "reading NVOFA capabilities")
        return list(values)

    def allocate(self, width, height, usage, format):
        handle = PTR()
        descriptor = BufferDescriptor(width, height, usage, format)
        check(self.create_buffer(self.handle, c.byref(descriptor), 2, c.byref(handle)),
              "allocating NVOFA buffer")
        self.buffers.append(handle)
        stride = StrideInfo()
        check(self.get_stride(handle, c.byref(stride)), "reading NVOFA buffer stride")
        pointer = self.get_pointer(handle)
        if not pointer or stride.numPlanes != 1:
            raise RuntimeError("Unexpected NVOFA buffer layout")
        return handle, pointer, stride.planes[0][0]

    def transfer(self, buffer, array, upload):
        _, pointer, pitch = buffer
        row_bytes = array.shape[1] * array.dtype.itemsize
        if array.ndim == 3:
            row_bytes *= array.shape[2]
        params = CudaCopy2D(widthBytes=row_bytes, height=array.shape[0])
        if upload:
            params.srcType, params.srcHost, params.srcPitch = 1, array.ctypes.data, row_bytes
            params.dstType, params.dstDevice, params.dstPitch = 2, pointer, pitch
        else:
            params.srcType, params.srcDevice, params.srcPitch = 2, pointer, pitch
            params.dstType, params.dstHost, params.dstPitch = 1, array.ctypes.data, row_bytes
        check(self.copy(c.byref(params)), "copying NVOFA buffer")

    def compute(self, source, target):
        """Return source->target and target->source float32 pixel flow + uint8 costs."""
        import cv2
        import numpy as np
        if not self.context:
            raise RuntimeError("NVOFA session is closed")
        for image in (source, target):
            if image.dtype != np.uint8 or image.shape != (self.height, self.width):
                raise ValueError("NVOFA input must be matching uint8 grayscale frames")
        with self.active():
            self.transfer(self.inputs[0], np.ascontiguousarray(source), True)
            self.transfer(self.inputs[1], np.ascontiguousarray(target), True)
            results = []
            for first, second in ((0, 1), (1, 0)):
                # Calls alternate direction and may revisit cached-window pairs.
                # Never reuse implicit temporal hints across these independent calls.
                inputs = ExecuteInput(inputFrame=self.inputs[first][0],
                    referenceFrame=self.inputs[second][0], disableTemporalHints=1)
                outputs = ExecuteOutput(outputBuffer=self.flow[0], outputCostBuffer=self.cost[0])
                check(self.execute(self.handle, c.byref(inputs), c.byref(outputs)), "NVOFA execution")
                check(self.synchronize(), "waiting for NVOFA")
                flow = np.empty((self.flow_height, self.flow_width, 2), np.int16)
                cost = np.empty((self.flow_height, self.flow_width), np.uint8)
                self.transfer(self.flow, flow, False)
                self.transfer(self.cost, cost, False)
                flow = flow.astype(np.float32) / 32  # S10.5 already measures input pixels.
                if self.grid != 1:
                    flow = cv2.resize(flow, (self.width, self.height), interpolation=cv2.INTER_LINEAR)
                    cost = cv2.resize(cost, (self.width, self.height), interpolation=cv2.INTER_NEAREST)
                results.append((flow, cost))
        return results[0][0], results[1][0], results[0][1], results[1][1]

    def close(self):
        if not self.context:
            return
        try:
            with self.active():
                self.synchronize()
                for buffer in reversed(self.buffers):
                    self.destroy_buffer(buffer)
                self.buffers.clear()
                if self.handle:
                    self.destroy(self.handle)
                    self.handle = PTR()
        finally:
            self.destroy_context(self.context)
            self.context = PTR()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def self_test():
    import numpy as np
    assert (c.sizeof(InitParams), c.sizeof(ExecuteInput), c.sizeof(ExecuteOutput),
            c.sizeof(CudaCopy2D), c.sizeof(StrideInfo)) == (48, 56, 24, 128, 28)
    source = np.random.default_rng(42).integers(0, 256, (192, 256), dtype=np.uint8)
    target = np.roll(source, (2, 6), axis=(0, 1))
    with NvidiaOpticalFlow(256, 192) as engine:
        forward, backward, cost, _ = engine.compute(source, target)
        interior = np.s_[32:-32, 32:-32]
        assert np.median(np.abs(forward[interior] - (6, 2))) < .5
        assert np.median(np.abs(backward[interior] + (6, 2))) < .5
        assert cost.shape == source.shape and cost.dtype == np.uint8
        stationary, _, _, _ = engine.compute(source, source)
        assert np.median(np.abs(stationary[interior])) < .1
        repeated, _, _, _ = engine.compute(source, target)
        assert np.median(np.abs(repeated[interior] - (6, 2))) < .5
        print(f"NVIDIA NVOFA hardware self-test passed: {engine.device_name}; "
              f"grid {engine.grid}x{engine.grid}; forward/backward translation verified")
    try:
        NvidiaOpticalFlow(256, 192, device=9999)
    except RuntimeError as error:
        assert "NVIDIA hardware optical flow is unavailable" in str(error)
    else:
        raise AssertionError("An unavailable NVIDIA device was silently accepted")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        raise SystemExit("Use --self-test to verify NVIDIA hardware optical flow")
