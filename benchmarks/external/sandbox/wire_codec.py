"""Bounded JSON value codec; no pickle, imports from payloads, or object arrays."""
import base64
import math
import numpy as np

MAX_ARRAY_BYTES = 16 * 1024 * 1024


def encode(value):
    if isinstance(value, np.ndarray):
        if value.dtype.kind not in "biufc" or value.nbytes > MAX_ARRAY_BYTES or not np.all(np.isfinite(value)):
            raise ValueError("Unsupported array")
        value = np.ascontiguousarray(value)
        return {"$array": {"data": base64.b64encode(value.tobytes()).decode(), "dtype": value.dtype.str, "shape": list(value.shape)}}
    if isinstance(value, np.generic):
        return encode(value.item())
    if isinstance(value, bytes):
        return {"$bytes": base64.b64encode(value).decode()}
    if isinstance(value, tuple):
        return {"$tuple": [encode(v) for v in value]}
    if isinstance(value, dict):
        if all(isinstance(k, str) and not k.startswith("$") for k in value):
            return {k: encode(v) for k, v in value.items()}
        return {"$mapping": [[encode(k), encode(v)] for k, v in value.items()]}
    if isinstance(value, list):
        return [encode(v) for v in value]
    if value is None or type(value) in (str, bool, int) or type(value) is float and math.isfinite(value):
        return value
    raise ValueError("Unsupported wire value")


def decode(value):
    if isinstance(value, list):
        return [decode(v) for v in value]
    if not isinstance(value, dict):
        if value is None or type(value) in (str, bool, int) or type(value) is float and math.isfinite(value):
            return value
        raise ValueError("Unsupported wire scalar")
    if set(value) == {"$bytes"}:
        return base64.b64decode(value["$bytes"], validate=True)
    if set(value) == {"$tuple"}:
        return tuple(decode(v) for v in value["$tuple"])
    if set(value) == {"$mapping"}:
        result = {}
        for key, item in value["$mapping"]:
            key = decode(key)
            if key in result:
                raise ValueError("Duplicate mapping key")
            result[key] = decode(item)
        return result
    if set(value) == {"$array"}:
        a = value["$array"]
        if not isinstance(a, dict) or set(a) != {"data", "dtype", "shape"} or not isinstance(a["shape"], list):
            raise ValueError("Invalid array envelope")
        dtype, shape = np.dtype(a["dtype"]), a["shape"]
        if dtype.kind not in "biufc" or len(shape) > 4 or any(type(n) is not int or n < 0 for n in shape):
            raise ValueError("Invalid array type/shape")
        size = math.prod(shape) * dtype.itemsize
        if size > MAX_ARRAY_BYTES or len(a["data"]) > 4 * ((MAX_ARRAY_BYTES + 2) // 3):
            raise ValueError("Array exceeds bound")
        data = base64.b64decode(a["data"], validate=True)
        if len(data) != size:
            raise ValueError("Array length mismatch")
        array = np.frombuffer(data, dtype=dtype).reshape(shape).copy()
        if not np.all(np.isfinite(array)):
            raise ValueError("Nonfinite array")
        return array
    if any(k.startswith("$") for k in value):
        raise ValueError("Unknown wire tag")
    return {k: decode(v) for k, v in value.items()}
