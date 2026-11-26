"""Test videos made with ffmpeg, and a minimal MP4 box reader, for the media library transcoding tests."""
import os
from pathlib import Path
import shutil
import struct
import subprocess

ASS = """[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,28,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:11.50,Default,,0,0,0,,{\\an8}内封字幕测试\\N第二行
"""


def tools():
    """ffmpeg and ffprobe from TEST_FFMPEG (a folder) or PATH, or None."""
    folder = os.environ.get('TEST_FFMPEG')
    exe = '.exe' if os.name == 'nt' else ''
    if folder:
        ffmpeg, ffprobe = Path(folder) / ('ffmpeg' + exe), Path(folder) / ('ffprobe' + exe)
        return (str(ffmpeg), str(ffprobe)) if ffmpeg.exists() and ffprobe.exists() else None
    ffmpeg, ffprobe = shutil.which('ffmpeg'), shutil.which('ffprobe')
    return (ffmpeg, ffprobe) if ffmpeg and ffprobe else None


def make(ffmpeg, folder, seconds=12):
    """Three 12 s episodes with a keyframe every 2 s: H.264/AAC with embedded ASS and a font, HEVC 10-bit/FLAC, VP9/FLAC."""
    folder = Path(folder)
    work = folder / '.fixtures'
    work.mkdir(parents=True, exist_ok=True)
    (work / 'sub.ass').write_text(ASS, encoding='utf-8')
    (work / 'font.ttf').write_bytes(b'\x00\x01\x00\x00' + b'\x00' * 60)  # placeholder: only its extraction is checked
    video = ['-f', 'lavfi', '-i', f'testsrc2=size=1280x720:rate=24:duration={seconds}', '-f', 'lavfi', '-i', f'sine=frequency=440:duration={seconds}']
    gop = ['-g', '48', '-keyint_min', '48']
    outputs = {
        'h264': ('作品甲', video + ['-i', str(work / 'sub.ass'), '-attach', str(work / 'font.ttf'), '-metadata:s:t', 'mimetype=application/x-truetype-font',
                                '-map', '0:v', '-map', '1:a', '-map', '2:s', '-c:v', 'libx264', '-preset', 'ultrafast', '-x264-params', 'keyint=48:min-keyint=48:scenecut=0',
                                '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-c:s', 'ass', '-metadata:s:s:0', 'language=chi', '-metadata:s:s:0', 'title=简体中文']),
        'hevc': ('作品乙', video + ['-map', '0:v', '-map', '1:a', '-c:v', 'libx265', '-preset', 'ultrafast', '-x265-params', 'keyint=48:min-keyint=48:scenecut=0:log-level=error',
                                '-pix_fmt', 'yuv420p10le', '-c:a', 'flac']),
        'vp9': ('作品丙', video + ['-map', '0:v', '-map', '1:a', '-c:v', 'libvpx-vp9', '-deadline', 'realtime', '-cpu-used', '8', '-b:v', '1M'] + gop + ['-c:a', 'flac']),
    }
    made = {}
    for key, (name, arguments) in outputs.items():
        target = folder / name / f'[Test] {name} - 01.mkv'
        target.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([ffmpeg, '-hide_banner', '-v', 'error', '-y'] + arguments + [str(target)], check=True, timeout=300)
        made[key] = target
    shutil.rmtree(work)
    return made


def boxes(data, start=0, end=None):
    end = len(data) if end is None else end
    while start + 8 <= end:
        size, kind = struct.unpack('>I4s', data[start:start + 8])
        header = 8
        if size == 1:
            size, header = struct.unpack('>Q', data[start + 8:start + 16])[0], 16
        elif size == 0:
            size = end - start
        yield kind.decode('latin-1'), start + header, start + size
        start += size


def child(data, start, end, kind):
    return next(((s, e) for k, s, e in boxes(data, start, end) if k == kind), None)


def timescales(init):
    """Track ID → media timescale, from the init segment's moov/trak/{tkhd,mdia/mdhd}."""
    moov = child(init, 0, len(init), 'moov')
    result = {}
    for kind, s, e in boxes(init, *moov):
        if kind != 'trak':
            continue
        tkhd = child(init, s, e, 'tkhd')
        version = init[tkhd[0]]
        track = struct.unpack('>I', init[tkhd[0] + (20 if version == 1 else 12):][:4])[0]
        mdia = child(init, s, e, 'mdia')
        mdhd = child(init, *mdia, 'mdhd')
        version = init[mdhd[0]]
        result[track] = struct.unpack('>I', init[mdhd[0] + (20 if version == 1 else 12):][:4])[0]
    return result


def start_times(init, segment):
    """Seconds at which each track of a media segment starts (moof/traf/tfdt divided by the track's timescale)."""
    scales = timescales(init)
    result = {}
    for kind, s, e in boxes(segment):
        if kind != 'moof':
            continue
        for k, ts, te in boxes(segment, s, e):
            if k != 'traf':
                continue
            tfhd = child(segment, ts, te, 'tfhd')
            track = struct.unpack('>I', segment[tfhd[0] + 4:tfhd[0] + 8])[0]
            tfdt = child(segment, ts, te, 'tfdt')
            version = segment[tfdt[0]]
            base = struct.unpack('>Q' if version == 1 else '>I', segment[tfdt[0] + 4:tfdt[0] + (12 if version == 1 else 8)])[0]
            result.setdefault(track, base / scales[track])
        break
    return result


def codecs(init):
    """Sample entry names in the init segment (avc1, hvc1, mp4a, fLaC…)."""
    return {name for name in ('avc1', 'avc3', 'hvc1', 'hev1', 'vp09', 'mp4a', 'fLaC', 'Opus') if name.encode() in init}


if __name__ == '__main__':
    import json
    import sys
    found = tools()
    if not found:
        sys.exit(3)
    if len(sys.argv) > 1:
        print(json.dumps({'ffmpeg': found[0], 'ffprobe': found[1], 'files': {k: str(v) for k, v in make(found[0], sys.argv[1]).items()}}, ensure_ascii=False))
    else:
        print(json.dumps({'ffmpeg': found[0], 'ffprobe': found[1]}))
