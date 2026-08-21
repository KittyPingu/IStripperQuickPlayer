#!/usr/bin/env python3
"""RVM-conditioned high-resolution prop recovery model and inference helpers."""
import math

ARCHITECTURE = "rvm-conditioned-convnext-fpn-v2"
INPUT_SIZE = 768
MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)
DISTANCE_CLIP_AT_INPUT = 96
ROI_EXPANSION = .35
MIN_CONTEXT_AT_INPUT = 128
MAX_TILES = 8


def _decoder_block(torch, inputs, outputs):
    return torch.nn.Sequential(
        torch.nn.Conv2d(inputs, outputs, 3, padding=1, bias=False),
        torch.nn.GroupNorm(16, outputs),
        torch.nn.GELU())


def build_model(torch, pretrained=False):
    """Build ConvNeXt-Tiny/FPN and expand its RGB stem to five channels."""
    from torchvision.models import ConvNeXt_Tiny_Weights, convnext_tiny

    class ConditionedFpn(torch.nn.Module):
        def __init__(self):
            super().__init__()
            weights = ConvNeXt_Tiny_Weights.IMAGENET1K_V1 if pretrained else None
            backbone = convnext_tiny(weights=weights)
            old = backbone.features[0][0]
            expanded = torch.nn.Conv2d(5, old.out_channels, old.kernel_size,
                old.stride, old.padding, bias=old.bias is not None)
            with torch.no_grad():
                expanded.weight.zero_()
                expanded.weight[:, :3].copy_(old.weight)
                if old.bias is not None: expanded.bias.copy_(old.bias)
            backbone.features[0][0] = expanded
            self.features = backbone.features
            channels = (96, 192, 384, 768)
            self.lateral = torch.nn.ModuleList([
                torch.nn.Conv2d(value, 128, 1) for value in channels])
            self.smooth = torch.nn.ModuleList([
                _decoder_block(torch, 128, 128) for _ in channels])
            self.fuse = torch.nn.Sequential(
                _decoder_block(torch, 128 * 4, 128),
                torch.nn.Conv2d(128, 1, 1))
            self.presence = torch.nn.Sequential(
                torch.nn.AdaptiveAvgPool2d(1), torch.nn.Flatten(),
                torch.nn.LayerNorm(channels[-1]), torch.nn.Linear(channels[-1], 1))

        def forward(self, inputs):
            original = inputs.shape[-2:]
            value, pyramid = inputs, []
            for index, layer in enumerate(self.features):
                value = layer(value)
                if index in (1, 3, 5, 7): pyramid.append(value)
            decoded = [None] * 4
            top = None
            for index in range(3, -1, -1):
                current = self.lateral[index](pyramid[index])
                if top is not None:
                    current = current + torch.nn.functional.interpolate(
                        top, size=current.shape[-2:], mode="bilinear", align_corners=False)
                top = self.smooth[index](current)
                decoded[index] = top
            stride4 = decoded[0].shape[-2:]
            fused = torch.cat([decoded[0]] + [torch.nn.functional.interpolate(
                value, size=stride4, mode="bilinear", align_corners=False)
                for value in decoded[1:]], dim=1)
            logits = torch.nn.functional.interpolate(self.fuse(fused), size=original,
                mode="bilinear", align_corners=False)
            return {"out": logits, "presence": self.presence(pyramid[-1])[:, 0]}

    return ConditionedFpn()


def signed_distance(alpha, threshold=.4, clip=DISTANCE_CLIP_AT_INPUT):
    """Return clipped signed distance: positive inside RVM, negative outside."""
    import cv2
    import numpy as np
    foreground = np.asarray(alpha, dtype=np.float32) >= float(threshold)
    inside = cv2.distanceTransform(foreground.astype(np.uint8), cv2.DIST_L2, 3)
    outside = cv2.distanceTransform((~foreground).astype(np.uint8), cv2.DIST_L2, 3)
    return np.clip(inside - outside, -clip, clip).astype(np.float32) / float(clip)


def conditioned_array(rgb, alpha, threshold=.4, distance_clip=DISTANCE_CLIP_AT_INPUT):
    import numpy as np
    rgb = np.asarray(rgb, dtype=np.float32) / 255.
    alpha = np.asarray(alpha, dtype=np.float32)
    if alpha.max(initial=0) > 1: alpha = alpha / 65535. if alpha.max() > 255 else alpha / 255.
    rgb = (rgb - np.asarray(MEAN, dtype=np.float32)) / np.asarray(STD, dtype=np.float32)
    distance = signed_distance(alpha, threshold, distance_clip)
    return np.concatenate((rgb.transpose(2, 0, 1), alpha[None], distance[None]), axis=0)


def expanded_roi(alpha, threshold=.4, expansion=ROI_EXPANSION,
                 minimum_context=MIN_CONTEXT_AT_INPUT):
    import numpy as np
    alpha = np.asarray(alpha)
    ys, xs = np.where(alpha >= threshold)
    height, width = alpha.shape
    if not len(xs): return 0, 0, width, height
    left, right, top, bottom = int(xs.min()), int(xs.max()) + 1, int(ys.min()), int(ys.max()) + 1
    pad_x = max(minimum_context, round((right - left) * expansion))
    pad_y = max(minimum_context, round((bottom - top) * expansion))
    return max(0, left - pad_x), max(0, top - pad_y), \
        min(width, right + pad_x), min(height, bottom + pad_y)


def _axis_tiles(start, end, limit, tile, overlap=.25):
    length = end - start
    if length <= tile: return [max(0, min(start - (tile - length) // 2, limit - tile))]
    step = max(1, round(tile * (1 - overlap)))
    values = list(range(start, max(start + 1, end - tile + 1), step))
    last = max(0, min(end - tile, limit - tile))
    if not values or values[-1] != last: values.append(last)
    return sorted(set(max(0, min(value, max(0, limit - tile))) for value in values))


def inference_tiles(alpha, size=INPUT_SIZE, maximum=MAX_TILES, threshold=.4):
    """Cover the expanded RVM ROI at native resolution, capped deterministically."""
    import numpy as np
    height, width = np.asarray(alpha).shape
    left, top, right, bottom = expanded_roi(alpha, threshold)
    xs, ys = _axis_tiles(left, right, width, min(size, width)), \
        _axis_tiles(top, bottom, height, min(size, height))
    tiles = [(x, y, min(width, x + size), min(height, y + size)) for y in ys for x in xs]
    if len(tiles) <= maximum: return tiles
    foreground_y, foreground_x = np.where(np.asarray(alpha) >= threshold)
    center_x = float(foreground_x.mean()) if len(foreground_x) else width / 2
    center_y = float(foreground_y.mean()) if len(foreground_y) else height / 2
    return sorted(tiles, key=lambda item: ((item[0] + item[2]) / 2 - center_x) ** 2 +
                  ((item[1] + item[3]) / 2 - center_y) ** 2)[:maximum]


def _padded_tile(image, alpha, bounds, size):
    import numpy as np
    left, top, right, bottom = bounds
    pixels = np.asarray(image, dtype=np.uint8)[top:bottom, left:right]
    mask = np.asarray(alpha, dtype=np.float32)[top:bottom, left:right]
    canvas = np.empty((size, size, 3), dtype=np.uint8)
    canvas[:] = tuple(round(value * 255) for value in MEAN)
    alpha_canvas = np.zeros((size, size), dtype=np.float32)
    height, width = pixels.shape[:2]
    canvas[:height, :width] = pixels; alpha_canvas[:height, :width] = mask
    return canvas, alpha_canvas, width, height


def predict(torch, model, device, image, rvm_alpha, size=INPUT_SIZE, threshold=.4):
    """Predict a source-sized prop probability map from RGB plus RVM alpha."""
    import numpy as np
    image = np.asarray(image, dtype=np.uint8)
    alpha = np.asarray(rvm_alpha, dtype=np.float32)
    if alpha.max(initial=0) > 1: alpha = alpha / 65535. if alpha.max() > 255 else alpha / 255.
    if image.shape[:2] != alpha.shape: raise RuntimeError("RGB and RVM alpha dimensions differ")
    height, width = alpha.shape
    probability_sum = np.zeros((height, width), dtype=np.float32)
    weight_sum = np.zeros_like(probability_sum)
    presences = []
    bf16 = device.type == "cuda" and torch.cuda.get_device_capability(device)[0] >= 8
    for bounds in inference_tiles(alpha, size, threshold=threshold):
        tile, tile_alpha, tile_width, tile_height = _padded_tile(image, alpha, bounds, size)
        inputs = torch.from_numpy(conditioned_array(tile, tile_alpha, threshold)).unsqueeze(0).to(device)
        with torch.inference_mode(), torch.autocast(device_type=device.type,
                dtype=torch.bfloat16, enabled=bf16):
            output = model(inputs)
        value = output["out"][0, 0, :tile_height, :tile_width].float().sigmoid().cpu().numpy()
        presences.append(float(output["presence"][0].float().sigmoid().cpu()))
        left, top, right, bottom = bounds
        wy = np.hanning(max(3, tile_height))[:tile_height]
        wx = np.hanning(max(3, tile_width))[:tile_width]
        weight = np.maximum(.05, wy[:, None] * wx[None, :]).astype(np.float32)
        probability_sum[top:bottom, left:right] += value * weight
        weight_sum[top:bottom, left:right] += weight
    probability = np.divide(probability_sum, weight_sum, out=np.zeros_like(probability_sum),
                            where=weight_sum > 0)
    return probability, max(presences, default=0.)


def filter_prediction(probability, rvm_alpha, pixel_threshold, presence,
                      presence_threshold=.5, distance_at_input=96, input_size=INPUT_SIZE):
    """Keep complete predicted components that approach the RVM foreground."""
    import cv2
    import numpy as np
    probability = np.asarray(probability, dtype=np.float32)
    person = np.asarray(rvm_alpha, dtype=np.float32) >= .4
    if presence < presence_threshold:
        return np.zeros_like(person), [], max(1, round(distance_at_input * min(person.shape) / input_size))
    prediction = probability >= pixel_threshold
    count, labels = cv2.connectedComponents(prediction.astype(np.uint8), connectivity=8)
    radius = max(1, round(distance_at_input * min(person.shape) / input_size))
    near = cv2.dilate(person.astype(np.uint8), np.ones((radius * 2 + 1,) * 2, np.uint8)) != 0
    keep = np.zeros(count, dtype=bool)
    touching = np.unique(labels[near & prediction]); keep[touching[touching > 0]] = True
    retained = keep[labels]
    sizes = np.bincount(labels.ravel(), minlength=count)
    components = [{"pixels": int(sizes[index]), "retained": bool(keep[index]),
                   "meanProbability": float(probability[labels == index].mean())}
                  for index in range(1, count)]
    return retained, components, radius


def confirm_temporal_masks(masks, high_confidence=None, required=2, match_radius=8):
    """Retain centre-frame components supported by nearby-frame evidence."""
    import cv2
    import numpy as np
    if not masks: raise ValueError("At least one temporal mask is required")
    values = np.stack([np.asarray(value, dtype=bool) for value in masks])
    if match_radius > 0:
        kernel = np.ones((match_radius * 2 + 1,) * 2, np.uint8)
        evidence = np.stack([cv2.dilate(value.astype(np.uint8), kernel) != 0
                             for value in values]).sum(axis=0) >= required
    else:
        evidence = values.sum(axis=0) >= required
    center = values[len(values) // 2]
    count, labels = cv2.connectedComponents(center.astype(np.uint8), connectivity=8)
    keep = np.zeros(count, bool)
    supported = np.unique(labels[evidence & center]); keep[supported[supported > 0]] = True
    if high_confidence is not None:
        immediate = np.unique(labels[np.asarray(high_confidence, dtype=bool) & center])
        keep[immediate[immediate > 0]] = True
    return keep[labels]


def self_test():
    import numpy as np
    alpha = np.zeros((64, 96), np.float32); alpha[16:48, 30:60] = 1
    distance = signed_distance(alpha, clip=16)
    assert distance[32, 45] > 0 and distance[0, 0] < 0
    assert expanded_roi(alpha, minimum_context=4) == (20, 5, 70, 59)
    tiles = inference_tiles(alpha, 32, maximum=8)
    assert tiles and len(tiles) <= 8
    array = conditioned_array(np.zeros((64, 96, 3), np.uint8), alpha)
    assert array.shape == (5, 64, 96)
    masks = [np.eye(4, dtype=bool), np.eye(4, dtype=bool), np.zeros((4, 4), bool)]
    assert confirm_temporal_masks(masks).sum() == 4
    print('{"status":"ok","message":"prop-segmenter-v2 self-test passed"}')


if __name__ == "__main__":
    import sys
    if sys.argv[1:] == ["--self-test"]: self_test()
    else: raise SystemExit("Use --self-test")
