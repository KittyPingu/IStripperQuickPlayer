from fractions import Fraction

from sam2matting_worker import (
    ALPHA_ENCODING_POLICY,
    CHECKPOINTS,
    CHECKPOINT_REVISION,
    SOURCE_REVISION,
    frame_rates_match,
    normalize_concepts,
    prune_sam2_tracking_state,
    release_sam2_frame_alpha,
    sam2_tracking_history,
    tracker_scene_chunk_size,
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


def test_h264_alpha_quantization_endpoints():
    assert ALPHA_ENCODING_POLICY == "h264-yuv420p-linear"
    values = [round(max(0.0, min(1.0, value)) * 255)
              for value in [0.0, 0.5, 1.0]]
    assert values == [0, 128, 255]


def test_matroska_millisecond_timebase_rounding_is_accepted():
    assert frame_rates_match("19001/317", "60000/1001")
    assert not frame_rates_match("25/1", "30/1")


def test_consumed_sam2_alpha_is_removed_from_tracking_state():
    retained = object()
    state = {
        "output_dict_per_obj": {0: {
            "cond_frame_outputs": {},
            "non_cond_frame_outputs": {
                3: {"alpha": retained, "pred_masks": retained},
                4: {"alpha": retained},
            },
        }},
        "temp_output_dict_per_obj": {},
    }
    release_sam2_frame_alpha(state, 3)
    assert state["output_dict_per_obj"][0]["non_cond_frame_outputs"][3]["alpha"] is None
    assert state["output_dict_per_obj"][0]["non_cond_frame_outputs"][3]["pred_masks"] is retained
    assert state["output_dict_per_obj"][0]["non_cond_frame_outputs"][4]["alpha"] is retained


def test_sam2_tracking_state_keeps_only_future_dependencies():
    class Predictor:
        num_maskmem = 7
        memory_temporal_stride_for_eval = 1
        max_obj_ptrs_in_encoder = 16

    assert sam2_tracking_history(Predictor()) == 15
    frames = {index: {"alpha": None} for index in range(100)}
    state = {
        "output_dict_per_obj": {0: {
            "cond_frame_outputs": {0: {}},
            "non_cond_frame_outputs": frames,
        }},
        "frames_tracked_per_obj": {0: {index: {} for index in range(100)}},
    }
    prune_sam2_tracking_state(state, 99, False, 15, (1, 16))
    remaining = state["output_dict_per_obj"][0]["non_cond_frame_outputs"]
    assert set(remaining) == set(range(1, 16)) | set(range(84, 100))
    prune_sam2_tracking_state(state, 0, True, 15)
    assert set(remaining) == set(range(1, 16))


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


def test_sam2_prefetch_uses_the_upcoming_scene_size():
    assert tracker_scene_chunk_size("sam2.1-tiny", 3840, 2160, 180) == 180
    assert tracker_scene_chunk_size("sam2.1-tiny", 3840, 2160, 315) == 315
    assert tracker_scene_chunk_size("sam2.1-base-plus", 3840, 2160, 315) == 315
