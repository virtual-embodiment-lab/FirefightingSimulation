"""Read-only scene/prefab and quaternion audit; does not launch Unity or reproduce a headset incident."""
import bisect
import json
import math
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def objects(path):
    text = (ROOT / path).read_text(encoding="utf-8-sig")
    matches = list(re.finditer(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\n", text, re.M))
    newlines = [m.start() for m in re.finditer("\n", text)]
    return {
        m.group(2): {
            "type": int(m.group(1)),
            "text": text[m.end():matches[i + 1].start() if i + 1 < len(matches) else len(text)],
            "line": bisect.bisect_right(newlines, m.end()) + 1,
        }
        for i, m in enumerate(matches)
    }


def field(text, name):
    match = re.search(r"^  " + re.escape(name) + r": (.*)$", text, re.M)
    return match.group(1) if match else ""


def reference(text, name):
    match = re.search(r"^  " + re.escape(name) + r": \{fileID: (-?\d+)", text, re.M)
    return match.group(1) if match else ""


def quaternion(text):
    return tuple(float(v) for v in re.findall(r"[xyzw]: ([^,}]+)", field(text, "m_LocalRotation")))


def multiply(a, b):
    x, y, z, w = a
    xx, yy, zz, ww = b
    return (w * xx + x * ww + y * zz - z * yy,
            w * yy - x * zz + y * ww + z * xx,
            w * zz + x * yy - y * xx + z * ww,
            w * ww - x * xx - y * yy - z * zz)


def world_rotation(data, transform_id):
    chain = []
    while transform_id and transform_id != "0":
        text = data[transform_id]["text"]
        chain.append(quaternion(text))
        transform_id = reference(text, "m_Father")
    result = (0, 0, 0, 1)
    for rotation in reversed(chain):
        result = multiply(result, rotation)
    return result


def angle(a, b):
    dot = abs(sum(x * y for x, y in zip(a, b)))
    norm = math.sqrt(sum(x * x for x in a) * sum(x * x for x in b))
    return math.degrees(2 * math.acos(min(1, dot / norm)))


def axis_angle(axis, degrees):
    radians = math.radians(degrees) / 2
    return (*[v * math.sin(radians) for v in axis], math.cos(radians))


def rotate(q, vector):
    result = multiply(multiply(q, (*vector, 0)), (-q[0], -q[1], -q[2], q[3]))
    return result[:3]


def face_yaw(anchor):
    # The horizontal portion of VRIK's Cross(anchor.right, root.up) + anchor.forward.
    right = rotate(anchor, (1, 0, 0))
    forward = rotate(anchor, (0, 0, 1))
    return math.degrees(math.atan2(-right[2] + forward[0], right[0] + forward[2]))


def root_correction(yaw):
    return yaw - 25 if yaw > 25 else yaw + 25 if yaw < -25 else 0


def main():
    scene = objects("Assets/Scenes/Scenarios/Base.unity")
    avatar = objects("Assets/Prefabs/Avatars/Resources/Avatar+Can.prefab")
    sample = objects("Assets/Samples/XR Interaction Toolkit/3.0.10/Starter Assets/Prefabs/XR Origin (XR Rig).prefab")
    head_target = scene["1419936370"]["text"]
    assert reference(head_target, "m_Father") == "1395329944"
    driver = sample["6232745470614056083"]["text"]
    assert field(driver, "m_IgnoreTrackingState") == "0"
    assert "1031966339891076899" in driver
    ik = avatar["7614112240696031636"]["text"]
    realtime = avatar["4202401760385949986"]["text"]
    assert "headTarget: {fileID: 995869260781392882}" in ik
    assert "_head: {fileID: 995869260781392882}" in realtime
    assert "headClampWeight: 0.6" in ik and "rotationWeight: 1" in ik
    assert any(re.search(r"propertyPath: _localAvatarPrefab\n.*?objectReference: \{fileID: [^\n]*guid: 84a8803db9df54adc984756713656345", item["text"], re.S)
               for item in scene.values() if item["type"] == 1001)

    bind_head = world_rotation(avatar, "7339935147457321875")
    offset = quaternion(head_target)
    rows = []
    for yaw in (0, 45, 90, 135, 180):
        standing = root_correction(face_yaw(axis_angle((0, 1, 0), yaw)))
        kneeling = [root_correction(face_yaw(axis_angle((1, 0, 0), lean))) for lean in (0, 45, 90)]
        assert all(abs(value) < 1e-9 for value in kneeling)
        rows.append({"head_yaw_degrees": yaw, "standing_root_correction_degrees": round(standing, 6),
                     "full_kneel_root_correction_for_0_45_90_degree_leans": [round(v, 6) for v in kneeling]})
    assert angle(offset, tuple(-v for v in offset)) < 1e-5
    result = {
        "basis_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "method": "Serialized configuration audit and isolated quaternion math, not a full VRIK/headset reproduction",
        "checks": {
            "scene_head_target_parent_is_hmd_camera": True,
            "normcore_and_vrik_share_avatar_head_target": True,
            "hmd_driver_respects_tracking_state_and_has_state_reference": True,
            "head_target_offset_is_unit_quaternion": abs(sum(x * x for x in offset) - 1) < 1e-6,
            "base_prefab_neutral_head_vs_tracking_offset_angle_degrees": angle(bind_head, offset),
            "quaternion_sign_change_orientation_angle_degrees": angle(offset, tuple(-v for v in offset)),
        },
        "head_target_offset_xyzw": offset,
        "base_prefab_bind_head_world_xyzw": bind_head,
        "isolated_heading_cases": rows,
    }
    destination = Path(__file__).with_name("audit-results.json")
    destination.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
