# Screen capture

The runner captures the TV's composited picture through Samsung's private
`libdisplay-capture-api` and uses only the Y (luma) plane.

## 1. API

```c
int ppicapture(RequestedData* rd, unsigned char* ybuf, unsigned char* cbuf, int bufsize);
// C++ symbol: _Z10ppicaptureP13RequestedDataPhS1_i
```

`RequestedData` is an opaque 512-byte struct. The fields used by `probe/FrameProbe.cs`
and `runner/Capture.cs` are:

| Offset | Value | Meaning |
|---|---|---|
| `0x00` | `w` | requested width (e.g. 960/1920) |
| `0x04` | `h` | requested height (e.g. 540/1080) |
| `0x1c` | `0` | — |
| `0x24` | `2` | format selector |
| `0x28` | `6` | format selector |
| `0x0c` | `ysz` | returned Y size (bytes) |
| `0x10` | `csz` | returned C size (bytes) |
| `0x14` | `yp` | returned Y pointer |
| `0x18` | `cp` | returned C pointer |
| `0x00/0x04` | `rw/rh` | returned width/height |

Typical results on a Q70A:

```text
r=0 720x540 ysz=518400 csz=518400     # gameplay
r=0 720x576 ysz=518400 csz=518400     # other source/mode
```

The returned `rw` (720) is the row stride used by the runner. `ysz = 518400`
means the buffer is bigger than `rw*rh`; the extra bytes are padding (or the
requested 960×540/1920×1080 buffer). The runner validates `(long)rw*rh <= ysz`
instead of `rh > requested height`, because the API can return a taller frame
than requested (e.g. 576 vs 540).

## 2. Why `/usr/bin/dotnet` is denied

Samsung's capture path checks the calling process against a whitelist. On a normal
launch it logs:

```text
checkProcessWhitelist: error [/usr/bin/dotnet] TZCAPTURE_ACCESS_DENIED
TZCAPTURE_Initialize: ACCESS_DENIED Process!!!!!! 6
ppicapture returns -1, ysz=0, csz=0
```

The check reads `/proc/self/cmdline` (argv[0]) and compares it with allowed names.
`libtzcapturec.so` (TEE) implements the secure path; `libdisplay-capture-api`
wraps it.

## 3. The workaround

Launch the runner with a whitelisted `argv[0]`:

```bash
exec -a /usr/bin/tzcapturesample /usr/bin/dotnet /opt/usr/subtitle-ocr/SubtitleRunner.dll 0
```

The helper does this inside its mount namespace:

```text
unshare -m /bin/sh -c '
  mount --make-rprivate / &&
  mount --bind /opt/usr/subtitle-ocr /run/subtitle-ocr &&
  mount -o remount,bind,ro /run/subtitle-ocr &&
  exec /usr/bin/bash -c "exec -a /usr/bin/tzcapturesample /usr/bin/dotnet /opt/usr/subtitle-ocr/SubtitleRunner.dll 0"
'
```

With this, the same capture call returns:

```text
r=0 720x540 ysz=518400 csz=518400
```

> A **script file** (`run-subtitle.sh`) is rejected by Tizen UEP (“the file is NOT
> signed”). Use `bash -c "exec -a ..."` inline, not a `.sh` file.

## 4. Frame layout

- Y plane: `rw × rh` bytes, row stride `rw`.
- On a 720×576 frame the first 576 rows are picture; the rest of `frame.y` is
  padding.
- `probe/FrameProbe.cs` writes `frame.y`, `frame.c`, `frame.meta` for debugging.
- The runner writes the same files plus a downscaled/processed `input.pgm`.

## 5. Failure modes

| Message | Meaning |
|---|---|
| `r=-1 ... ysz=0` | capture denied or display not ready |
| `TZCAPTURE_ACCESS_DENIED` | process not started with the whitelisted argv[0] |
| `r=0`, but no text | frame captured; region/OCR issue (see OCR.md) |
| capture works, then stops | TV input/HDCP/secure state changed; restart the runner or reboot the TV |

Rebooting the TV can clear a stale secure-capture state. The runner/overlay should
be restarted afterwards (the helper is transient; open the app).

## 6. Notes and limits

- The capture is **not** a screen recorder: it is the video/composition layer used
  by the TV's capture API. Some OSD layers (menus, subtitles drawn by the TV itself)
  may or may not appear.
- The API is proprietary; behaviour varies by model/firmware.
- HDR/VRR/120 Hz content can change the frame size or make capture fail.
- This is a device-local bypass for research/personal use; do not ship it to
  devices you do not own.
