"""Process-bound Windows desktop smoke test for AI CutPro GUI.

GitHub hosted runners may lack an interactive desktop. An unavailable desktop
is reported, never interpreted as proof the GUI passed F1 acceptance.
"""
import pathlib
import subprocess
import sys
import time

from pywinauto import Desktop, keyboard



def probe_tab_navigation(window):
    """Exercise a real UIA button and observe the resulting status content."""
    def ui_text():
        return [x.window_text() for x in window.descendants(control_type="Text")]

    def button_named(label):
        matches = [b for b in window.descendants(control_type="Button")
                   if b.window_text() == label]
        if len(matches) != 1:
            raise RuntimeError(
                f"Expected one visible GUI button {label!r}, got {len(matches)}")
        return matches[0]

    def invoke(button):
        # UIA InvokePattern works for Avalonia controls without depending on
        # scroll positions or simulated screen coordinates.
        button.iface_invoke.Invoke()

    def assert_status(fragment):
        for _ in range(15):
            if any(fragment in text for text in ui_text()):
                return
            time.sleep(0.3)
        raise RuntimeError(
            f"GUI did not update text after interaction: {fragment!r}")

    invoke(button_named("История"))
    assert_status("Раздел «История»")
    assert_status("История")
    invoke(button_named("Раскрой"))
    assert_status("Рабочий прототип раскроя")
    print("GUI interaction PASS: History -> Layout tabs and live status changes")

    if button_named("Запуск резки (недоступен)").is_enabled():
        raise RuntimeError("Unsafe machine-start action unexpectedly enabled")
    print("GUI safety PASS: machine-start control remains disabled")

    invoke(button_named("Отменить изменение"))
    assert_status("Нет изменений DXF для отмены.")
    print("GUI undo PASS: empty edit history rejected with a visible message")
    invoke(button_named("Диагностика DXF (все замечания)"))
    diagnostic_window = None
    for _ in range(20):
        candidates = [w for w in Desktop(backend="uia").windows()
                      if w.process_id() == window.process_id()
                      and "Диагностика DXF" in w.window_text()]
        if candidates:
            diagnostic_window = candidates[0]
            break
        time.sleep(0.3)
    if diagnostic_window is None or not diagnostic_window.is_visible():
        raise RuntimeError("DXF diagnostics window did not open")
    details = [x.window_text() for x in
               diagnostic_window.descendants(control_type="Text")]
    if not any("нет диагностических записей" in t.lower() or
               "Пока нет диагностических записей" in t for t in details):
        raise RuntimeError("Empty diagnostics window has no explanatory guidance")
    diagnostic_window.close()
    print("GUI diagnostic panel PASS: opened separate offline report and empty-state guidance")
    # Real invalid-content DXF import through the native file chooser.
    # The extension is .dxf but the numerical coordinate is not a number.
    bad_dxf = pathlib.Path.cwd() / "dist" / "gui_invalid_coordinate.dxf"
    bad_dxf.parent.mkdir(parents=True, exist_ok=True)
    bad_dxf.write_text(
        "0\\nSECTION\\n2\\nENTITIES\\n0\\nLINE\\n8\\n0\\n"
        "10\\nNOT_A_NUMBER\\n20\\n0\\n11\\n20\\n21\\n0\\n"
        "0\\nENDSEC\\n0\\nEOF\\n", encoding="ascii")
    before_dialog = {w.handle for w in Desktop(backend="uia").windows()}
    invoke(button_named("Открыть DXF (предпросмотр)"))
    picker = None
    for _ in range(30):
        windows = Desktop(backend="uia").windows()
        choices = [w for w in windows if w.handle not in before_dialog
                   and w.is_visible()
                   and (w.class_name() == "#32770"
                        or "Открыть DXF" in w.window_text()
                        or w.window_text().lower() in ("open", "открытие"))]
        if choices:
            picker = choices[0]
            break
        time.sleep(0.25)
    if picker is None:
        raise RuntimeError("Windows DXF file picker was not discovered")
    picker.set_focus()
    # The Windows standard file dialog accepts the full absolute path in
    # its File name field; Alt+N is the native File name mnemonic.
    keyboard.send_keys("%n")
    keyboard.send_keys(str(bad_dxf), with_spaces=True)
    keyboard.send_keys("{ENTER}")
    assert_status("DXF-1003")
    invoke(button_named("Диагностика DXF (все замечания)"))
    report = None
    for _ in range(20):
        candidates = [w for w in Desktop(backend="uia").windows()
                      if w.process_id() == window.process_id()
                      and "Диагностика DXF" in w.window_text()]
        if candidates:
            report = candidates[0]
            break
        time.sleep(0.3)
    if report is None:
        raise RuntimeError("Report with invalid DXF finding did not open")
    texts = [x.window_text() for x in report.descendants(control_type="Text")]
    if not any("DXF-1003" in t for t in texts):
        raise RuntimeError("Report failed to show the structured malformed-DXF code")
    if not any("Как исправить:" in t for t in texts):
        raise RuntimeError("Report failed to show in-app repair guidance")
    report.close()
    print("GUI malformed DXF PASS: invalid coordinate rejected with code and repair guidance")




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
                    probe_tab_navigation(window)
                    print(
                        f"GUI smoke passed: title={window.window_text()!r}, "
                        f"pid={process.pid}, handle={window.handle}, "
                        f"size={capture.width}x{capture.height}, screenshot={screenshot}"
                    )
                    return
            except Exception as exc:
                if screenshot.is_file():
                    raise RuntimeError("GUI interaction failed after screenshot: " + str(exc)) from exc
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
