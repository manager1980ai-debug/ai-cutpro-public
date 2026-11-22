"""Process-bound Windows desktop smoke test for AI CutPro GUI.

GitHub hosted runners may lack an interactive desktop. An unavailable desktop
is reported, never interpreted as proof the GUI passed F1 acceptance.
"""
import pathlib
import subprocess
import sys
import time

from pywinauto import Desktop


def main() -> None:
    # GitHub-hosted Windows consoles can default to a legacy codepage; the
    # Russian title is valid UI data and must not make logging fail.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    executable = pathlib.Path(sys.argv[1]).resolve()
    screenshot = pathlib.Path(sys.argv[2]).resolve()
    if not executable.is_file():
        raise RuntimeError(f"Executable not found: {executable}")
    process = subprocess.Popen([str(executable)], cwd=executable.parent)
    try:
        for _ in range(30):
            if process.poll() is not None:
                raise RuntimeError(
                    f"AI CutPro exited before showing window: {process.returncode}")
            try:
                windows = Desktop(backend="uia").windows()
                matches = [
                    w for w in windows
                    if "AI CutPro" in w.window_text()
                    and w.process_id() == process.pid
                ]
                if matches:
                    window = matches[0]
                    if not window.is_visible():
                        raise RuntimeError("AI CutPro UIA window is not visible")
                    rect = window.rectangle()
                    if rect.width() < 900 or rect.height() < 600:
                        raise RuntimeError("AI CutPro main window is not laid out")
                    # Verify the actual launched process rather than any stale
                    # window with the same title from a previous CI attempt.
                    if window.process_id() != process.pid:
                        raise RuntimeError("UIA window is owned by another process")
                    screenshot.parent.mkdir(parents=True, exist_ok=True)
                    capture = window.capture_as_image()
                    if capture.width < 900 or capture.height < 600:
                        raise RuntimeError("Captured GUI dimensions are too small")
                    capture.save(screenshot)
                    print(
                        f"GUI smoke passed: title={window.window_text()!r}, "
                        f"pid={process.pid}, handle={window.handle}, "
                        f"size={capture.width}x{capture.height}, screenshot={screenshot}"
                    )
                    return
            except Exception as exc:
                print(f"UIA probe not ready: {exc}")
            time.sleep(1)
        raise RuntimeError("AI CutPro UIA main window not discovered within 30s")
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()


if __name__ == "__main__":
    main()
