#!/bin/sh
set -eu

render_device="${JELLYFIN_QSV_RENDER_DEVICE:-/dev/dri/renderD128}"

if [ -x /usr/lib/jellyfin-ffmpeg/ffmpeg ]; then
    ffmpeg_bin=/usr/lib/jellyfin-ffmpeg/ffmpeg
else
    ffmpeg_bin="$(command -v ffmpeg || true)"
fi

if [ -z "$ffmpeg_bin" ]; then
    echo "FFmpeg is missing" >&2
    exit 1
fi

driver_path="$(find /usr/lib -type f -name iHD_drv_video.so -print -quit 2>/dev/null)"
if [ -z "$driver_path" ]; then
    echo "Intel iHD VAAPI driver is missing" >&2
    exit 1
fi

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
