#!/bin/sh
set -eu

render_device="${JELLYFIN_QSV_RENDER_DEVICE:-/dev/dri/renderD128}"

ffmpeg_bin=/usr/lib/jellyfin-ffmpeg/ffmpeg
ffprobe_bin=/usr/lib/jellyfin-ffmpeg/ffprobe

if [ ! -x "$ffmpeg_bin" ] || [ ! -x "$ffprobe_bin" ]; then
    echo "Bundled FFmpeg or FFprobe is missing" >&2
    exit 1
fi

ffmpeg_path="$(command -v ffmpeg || true)"
ffprobe_path="$(command -v ffprobe || true)"
if [ -z "$ffmpeg_path" ] || [ -z "$ffprobe_path" ]; then
    echo "Bare ffmpeg or ffprobe is not available on PATH" >&2
    exit 1
fi

if [ "$(readlink -f "$ffmpeg_path")" != "$ffmpeg_bin" ] \
    || [ "$(readlink -f "$ffprobe_path")" != "$ffprobe_bin" ]; then
    echo "Bare ffmpeg or ffprobe does not resolve to the bundled Jellyfin runtime" >&2
    exit 1
fi

# Plugins such as IntroSkipper invoke these tools by bare command name rather
# than reading JELLYFIN_FFMPEG. Exercise that exact compatibility contract.
ffmpeg -hide_banner -version >/dev/null
ffprobe -hide_banner -version >/dev/null

driver_path="$(find /usr/lib -type f -name iHD_drv_video.so -print -quit 2>/dev/null)"
if [ -z "$driver_path" ]; then
    echo "Intel iHD VAAPI driver is missing" >&2
    exit 1
fi

if [ "${LIBVA_DRIVER_NAME:-}" != "iHD" ]; then
    echo "LIBVA_DRIVER_NAME must select the Intel iHD driver" >&2
    exit 1
fi

"$ffmpeg_bin" -hide_banner -hwaccels 2>/dev/null | grep -qx 'vaapi'
"$ffmpeg_bin" -hide_banner -hwaccels 2>/dev/null | grep -qx 'qsv'
"$ffmpeg_bin" -hide_banner -encoders 2>/dev/null | grep -Eq '^[[:space:]]*V[^[:space:]]*[[:space:]]+h264_qsv[[:space:]]'
"$ffmpeg_bin" -hide_banner -decoders 2>/dev/null | grep -Eq '^[[:space:]]*V[^[:space:]]*[[:space:]]+hevc_qsv[[:space:]]'

if [ "${1:-}" != "--hardware" ]; then
    printf 'QSV runtime contract present: ffmpeg=%s driver=%s device-check=skipped\n' "$ffmpeg_bin" "$driver_path"
    exit 0
fi

if [ ! -r "$render_device" ] || [ ! -w "$render_device" ]; then
    echo "QSV render device is not accessible: $render_device" >&2
    exit 1
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT HUP INT TERM

# Produce a deterministic, tiny HEVC Main10 source in software, then exercise
# the same decode/encode path Jellyfin uses for 2160p HEVC-to-H.264 playback.
"$ffmpeg_bin" -nostdin -hide_banner -loglevel error \
    -f lavfi -i 'testsrc2=size=128x72:rate=24:duration=1' \
    -pix_fmt yuv420p10le -c:v libx265 -preset ultrafast \
    -x265-params 'log-level=error:pools=1:frame-threads=1' \
    -an -y "$work_dir/main10.mkv"

LIBVA_DRIVER_NAME=iHD "$ffmpeg_bin" -nostdin -hide_banner -loglevel error \
    -init_hw_device "vaapi=va:${render_device},driver=iHD" \
    -init_hw_device qsv=qs@va -filter_hw_device qs \
    -hwaccel qsv -hwaccel_output_format qsv -c:v hevc_qsv \
    -i "$work_dir/main10.mkv" -map 0:v:0 -frames:v 12 \
    -vf 'scale_qsv=format=nv12' -c:v h264_qsv -f null -

printf 'QSV hardware transcode passed: driver=%s device=%s\n' "$driver_path" "$render_device"
