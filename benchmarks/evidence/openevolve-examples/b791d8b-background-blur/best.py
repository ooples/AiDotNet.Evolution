"""
Portrait-mode background blur: separable float32 Gaussian + mask composite.
"""

import numpy as np


# EVOLVE-BLOCK-START
def gaussian_kernel_1d(sigma: float) -> np.ndarray:
    """Truncated (3 sigma) normalised 1D Gaussian."""
    radius = int(np.ceil(3.0 * sigma))
    x = np.arange(-radius, radius + 1, dtype=np.float64)
    k = np.exp(-(x**2) / (2.0 * sigma**2))
    return k / k.sum()


def _conv_axis(img: np.ndarray, k: np.ndarray, axis: int, tmp: np.ndarray) -> np.ndarray:
    """1D symmetric convolution along axis (0 or 1) with reflect padding."""
    r = len(k) // 2
    n = img.shape[axis]
    pad = [(0, 0)] * img.ndim
    pad[axis] = (r, r)
    p = np.pad(img, pad, mode="reflect")

    def sl(start):
        idx = [slice(None)] * img.ndim
        idx[axis] = slice(start, start + n)
        return p[tuple(idx)]

    out = sl(r) * k[r]
    for i in range(1, r + 1):
        np.add(sl(r + i), sl(r - i), out=tmp)
        tmp *= k[r + i]
        out += tmp
    return out


def gaussian_blur(image: np.ndarray, sigma: float, k=None) -> np.ndarray:
    """Separable Gaussian blur of an (H, W, 3) image."""
    if k is None:
        k = gaussian_kernel_1d(sigma)
    k = k.astype(np.float32)
    img = np.ascontiguousarray(image, dtype=np.float32)
    tmp = np.empty_like(img)
    h = _conv_axis(img, k, 1, tmp)
    return _conv_axis(h, k, 0, tmp)


def blur_background(frame: np.ndarray, mask: np.ndarray, sigma: float, k=None) -> np.ndarray:
    """Blur the background of one frame, keeping the person sharp."""
    f = np.ascontiguousarray(frame, dtype=np.float32)
    blurred = gaussian_blur(f, sigma, k)
    m = mask.astype(np.float32)[:, :, None]
    # out = blurred + m * (frame - blurred)
    f -= blurred if False else 0  # no-op; keep f intact
    d = f - blurred
    d *= m
    d += blurred
    return d


def process_sequence(frames, masks, sigma: float):
    """
    frames: list of (H, W, 3) float32 arrays in [0, 1]
    masks:  list of (H, W)    float32 arrays in [0, 1], 1 = person
    returns: list of (H, W, 3) float32 arrays
    """
    k = gaussian_kernel_1d(sigma)
    return [blur_background(f, m, sigma, k) for f, m in zip(frames, masks)]


# EVOLVE-BLOCK-END