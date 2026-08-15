from fractions import Fraction

from sam2matting_worker import (
    CHECKPOINTS,
    CHECKPOINT_REVISION,
    SOURCE_REVISION,
    normalize_concepts,
    validate_scene_contract,
)


def test_pinned_contract_rejects_sam31():
    assert SOURCE_REVISION == "73dd721d77b56749248aefe5e8824d7f61b9d13c"
    assert CHECKPOINT_REVISION == "4315db9c60d27fde396b09765748a0ca6c97bed5"
    assert set(CHECKPOINTS) == {"sam2.1-tiny", "sam2.1-base-plus", "sam3"}
    assert all("SAM3.1" not in item[0] for item in CHECKPOINTS.values())


def test_float_max_union_never_adds_alpha():
    first = [0.2, 0.8]
    second = [0.7, 0.6]
    assert [max(a, b) for a, b in zip(first, second)] == [0.7, 0.8]


def test_gray16_quantization_endpoints():
    values = [round(max(0.0, min(1.0, value)) * 65535)
              for value in [0.0, 0.5, 1.0]]
    assert values == [0, 32768, 65535]


def test_concepts_are_trimmed_and_deduplicated_in_first_seen_order():
    assert normalize_concepts([
        " person ", "Bicycle", "PERSON", "", "handbag held by a person"
    ]) == ["person", "Bicycle", "handbag held by a person"]
    try:
        normalize_concepts(["", "   "])
        assert False, "an empty normalized concept list must be rejected"
    except RuntimeError:
        pass


def test_scene_contract_requires_exact_gap_free_clip_coverage():
    request = {
        "clips": [{"id": "clip", "startMs": 0, "endMs": 1000}],
        "scenes": [
            {"id": "a", "clipId": "clip", "startFrame": 0,
             "endFrameExclusive": 10},
            {"id": "b", "clipId": "clip", "startFrame": 10,
             "endFrameExclusive": 25},
        ],
    }
    validate_scene_contract(request, Fraction(25, 1))
    request["scenes"][1]["startFrame"] = 11
    try:
        validate_scene_contract(request, Fraction(25, 1))
        assert False, "a scene-plan gap must be rejected"
    except RuntimeError:
        pass
