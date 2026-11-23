"""Process-bound Windows desktop smoke test for AI CutPro GUI.

GitHub hosted runners may lack an interactive desktop. An unavailable desktop
is reported, never interpreted as proof the GUI passed F1 acceptance.
"""
import pathlib
import subprocess
import sys
import time

from pywinauto import Desktop



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
    invoke(button_named("Технологические линии (предпросмотр)"))
    preview_window = None
    for _ in range(20):
        windows = [w for w in Desktop(backend="uia").windows()
                   if w.process_id() == window.process_id()
                   and "Технологические линии" in w.window_text()]
        if windows:
            preview_window = windows[0]
            break
        time.sleep(0.25)
    if preview_window is None:
        raise RuntimeError("Sheet operations preview did not open")
    preview_texts = [w.window_text() for w in
                     preview_window.descendants(control_type="Text")]
    if not any("Предпросмотр: 1 сегментов" in t for t in preview_texts):
        raise RuntimeError("5-mm inset sample open-cut preview was not generated")
    if not any("резка запрещена" in t.lower() for t in preview_texts):
        raise RuntimeError("Sheet operation preview lacked machine safety notice")
    preview_window.close()
    print("GUI operation preview PASS: editable open-cut preview without machine control")




def probe_malformed_dxf(executable):
    """Check populated diagnostics from real DXF bytes, not a mocked report."""
    bad_dxf = pathlib.Path.cwd() / "dist" / "gui_invalid_coordinate.dxf"
    bad_dxf.parent.mkdir(parents=True, exist_ok=True)
    bad_dxf.write_text(
        "0\\nSECTION\\n2\\nENTITIES\\n0\\nLINE\\n8\\n0\\n"
        "10\\nNOT_A_NUMBER\\n20\\n0\\n11\\n20\\n21\\n0\\n"
        "0\\nENDSEC\\n0\\nEOF\\n", encoding="ascii")
    # The normal --open-dxf launch option uses the exact import/diagnostic
    # pipeline as the file picker, without depending on runner dialog UIA.
    launched = subprocess.Popen(
        [str(executable), "--open-dxf", str(bad_dxf)],
        cwd=executable.parent)
    try:
        app = None
        for _ in range(40):
            if launched.poll() is not None:
                raise RuntimeError("AI CutPro exited during malformed DXF import")
            candidates = [w for w in Desktop(backend="uia").windows()
                          if w.process_id() == launched.pid
                          and "AI CutPro" in w.window_text()]
            if candidates:
                app = candidates[0]
                break
            time.sleep(0.25)
        if app is None:
            raise RuntimeError("DXF command-line import main window missing")
        for _ in range(30):
            texts = [x.window_text() for x in app.descendants(control_type="Text")]
            if any("DXF-1003" in text for text in texts):
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("Malformed DXF did not produce DXF-1003 in GUI")
        buttons = [b for b in app.descendants(control_type="Button")
                   if b.window_text() == "Диагностика DXF (все замечания)"]
        if len(buttons) != 1:
            raise RuntimeError("Missing diagnostics button after failed import")
        buttons[0].iface_invoke.Invoke()
        report = None
        for _ in range(30):
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == launched.pid
                       and "Диагностика DXF" in w.window_text()]
            if matches:
                report = matches[0]
                break
            time.sleep(0.25)
        if report is None:
            raise RuntimeError("Populated diagnostic report did not open")
        texts = [x.window_text() for x in report.descendants(control_type="Text")]
        if not any("DXF-1003" in t for t in texts):
            raise RuntimeError("Populated report lacks code DXF-1003")
        if not any("Как исправить:" in t for t in texts):
            raise RuntimeError("Populated report lacks repair guidance")
        report.close()
        print("GUI malformed DXF PASS: actual invalid data, DXF-1003 and repair guidance")
    finally:
        launched.terminate()
        try:
            launched.wait(timeout=5)
        except subprocess.TimeoutExpired:
            launched.kill()




def probe_open_contour_layer(executable):
    """A real open LINE in a known DXF layer must identify that layer."""
    path = pathlib.Path.cwd() / "dist" / "gui_open_contour_layer.dxf"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n"
        "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nCUT_TEST\n"
        "10\n0\n20\n0\n11\n100\n21\n0\n0\nENDSEC\n0\nEOF\n",
        encoding="ascii")
    launched = subprocess.Popen([str(executable), "--open-dxf", str(path)],
                                cwd=executable.parent)
    try:
        app = None
        for _ in range(40):
            if launched.poll() is not None:
                raise RuntimeError("AI CutPro closed before open-contour inspection")
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == launched.pid
                       and "AI CutPro" in w.window_text()]
            if matches:
                app = matches[0]
                break
            time.sleep(0.25)
        if app is None:
            raise RuntimeError("Open-contour DXF GUI not found")
        for _ in range(30):
            texts = [x.window_text() for x in app.descendants(control_type="Text")]
            if any("GEO-3001" in t for t in texts):
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("Open contour did not produce GEO-3001")
        buttons = [b for b in app.descendants(control_type="Button")
                   if b.window_text() == "Диагностика DXF (все замечания)"]
        if len(buttons) != 1:
            raise RuntimeError("Diagnostics button not found on open-contour GUI")
        buttons[0].iface_invoke.Invoke()
        report = None
        for _ in range(30):
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == launched.pid
                       and "Диагностика DXF" in w.window_text()]
            if matches:
                report = matches[0]
                break
            time.sleep(0.25)
        if report is None:
            raise RuntimeError("Open contour diagnostic window missing")
        items = [x for x in report.descendants(control_type="ListItem")
                 if "GEO-3001" in x.window_text() and "CUT_TEST" in x.window_text()]
        if len(items) != 1:
            raise RuntimeError("Report missing layer-specific GEO-3001 list item")
        # Select the matching issue first; details describe the selected entry.
        items[0].iface_selection_item.Select()
        for _ in range(15):
            texts = [x.window_text() for x in report.descendants(control_type="Text")]
            if any("Слой: CUT_TEST" in t for t in texts):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Confirmed layer not displayed after selecting finding")
        if not any("Координаты проблемы: не определены" in t for t in texts):
            raise RuntimeError("Unconfirmed coordinates not explicitly marked unknown")
        report.close()
        print("GUI layer diagnosis PASS: GEO-3001, CUT_TEST and unknown coordinates")
    finally:
        launched.terminate()
        try:
            launched.wait(timeout=5)
        except subprocess.TimeoutExpired:
            launched.kill()



def probe_selected_dxf_line(executable):
    """Real DXF LINE from a specific layer becomes the preview input."""
    path = pathlib.Path.cwd() / "dist" / "gui_line_operation.dxf"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n"
        "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nCUT_SAMPLE\n"
        "10\n15\n20\n35\n11\n180\n21\n35\n"
        "0\nLINE\n8\nCUT_SAMPLE\n10\n180\n20\n35\n11\n180\n21\n75\n"
        "0\nARC\n8\nCURVES\n10\n60\n20\n50\n40\n20\n50\n0\n51\n90\n"
        "0\nLWPOLYLINE\n8\nCURVES\n90\n2\n70\n0\n"
        "10\n20\n20\n20\n42\n1\n10\n60\n20\n20\n"
        "0\nENDSEC\n0\nEOF\n",
        encoding="ascii")
    launched = subprocess.Popen([str(executable), "--open-dxf", str(path)],
                                cwd=executable.parent)
    try:
        app = None
        for _ in range(40):
            if launched.poll() is not None:
                raise RuntimeError("AI CutPro exited before LINE preview")
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == launched.pid
                       and "AI CutPro" in w.window_text()]
            if matches:
                app = matches[0]
                break
            time.sleep(0.25)
        if app is None:
            raise RuntimeError("DXF LINE preview app was not found")
        buttons = [b for b in app.descendants(control_type="Button")
                   if b.window_text() == "Технологические линии (предпросмотр)"]
        if len(buttons) != 1:
            raise RuntimeError("DXF LINE preview button missing")
        buttons[0].iface_invoke.Invoke()
        operation_window = None
        for _ in range(30):
            found = [w for w in Desktop(backend="uia").windows()
                     if w.process_id() == launched.pid
                     and "Технологические линии" in w.window_text()]
            if found:
                operation_window = found[0]
                break
            time.sleep(0.25)
        if operation_window is None:
            raise RuntimeError("DXF LINE operation editor did not open")
        entries = [x.window_text() for x in
                   operation_window.descendants(control_type="Text")]
        if not any("CUT_SAMPLE" in item and "15" in item and "180" in item
                   for item in entries):
            # A ComboBox's items may not appear as Text until the list is open.
            combo = operation_window.descendants(control_type="ComboBox")
            if not combo:
                raise RuntimeError("DXF LINE selector absent")
            combo[0].expand()
            entries = [x.window_text() for x in
                       operation_window.descendants(control_type="ListItem")]
            if not any("CUT_SAMPLE" in item for item in entries):
                raise RuntimeError("Imported DXF LINE not offered in selector")
            combo[0].collapse()
        select_first = [button for button in operation_window.descendants(control_type="Button")
                        if button.window_text() == "Применить выбранную DXF LINE"]
        if len(select_first) != 1:
            raise RuntimeError("DXF LINE explicit selection button missing")
        select_first[0].iface_invoke.Invoke()
        for _ in range(15):
            details = [item.window_text() for item in
                       operation_window.descendants(control_type="Text")]
            if any("Источник DXF LINE №1" in value and "CUT_SAMPLE" in value
                   and "X 15" in value and "X 180" in value
                   for value in details):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError(
                "DXF LINE selection did not report imported endpoints: " +
                repr(details))
        # Verify the actual editor inputs, not just a success message.
        # Edit controls are ordered by the four coordinate fields, followed
        # by the inset/cut/skip parameters in SheetOperationsWindow.
        edits = operation_window.descendants(control_type="Edit")
        if len(edits) < 8:
            raise RuntimeError(
                f"Expected eight numeric editor fields, found {len(edits)}")
        coordinate_values = []
        for field in edits[:4]:
            try:
                raw_value = field.iface_value.CurrentValue
            except Exception as exc:
                raise RuntimeError(
                    "Numeric coordinate field has no readable UIA value") from exc
            coordinate_values.append(raw_value)
        try:
            actual = tuple(float(value.replace(",", ".")) for value in coordinate_values)
        except ValueError as exc:
            raise RuntimeError(
                "Selected DXF coordinates not numeric: " + repr(coordinate_values)) from exc
        if actual != (15.0, 35.0, 180.0, 35.0):
            raise RuntimeError(
                "DXF selection did not populate coordinate text boxes: "
                + repr(coordinate_values))
        preset_buttons = [b for b in operation_window.descendants(control_type="Button")
                          if b.window_text() == "Применить шаблон к выбранной линии"]
        if len(preset_buttons) != 1:
            raise RuntimeError("DXF LINE preset action not found")
        preset_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Шаблон подготовлен" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Open-cut preset did not update the editor preview")
        chain_buttons = [b for b in operation_window.descendants(control_type="Button")
                         if b.window_text() == "Предпросмотр связанной цепочки DXF LINE"]
        if len(chain_buttons) != 1:
            raise RuntimeError("Open DXF LINE chain preview button missing")
        chain_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Предпросмотр цепочки: 2 DXF LINE" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Connected DXF LINE chain did not preview in GUI")
        chain_assign = [b for b in operation_window.descendants(control_type="Button")
                        if b.window_text() == "Назначить открытую цепочку (группа)"]
        chain_clear = [b for b in operation_window.descendants(control_type="Button")
                       if b.window_text() == "Снять назначение цепочки"]
        if len(chain_assign) != 1 or len(chain_clear) != 1:
            raise RuntimeError("Chain assignment or removal action missing")
        chain_assign[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Сохранено цепочек: 1" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Connected DXF chain was not assigned")
        chain_clear[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Осталось цепочек: 0" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Connected DXF chain was not removed")
        assign_buttons = [b for b in operation_window.descendants(control_type="Button")
                          if b.window_text() == "Назначить операцию выбранной DXF LINE (сеанс)"]
        if len(assign_buttons) != 1:
            raise RuntimeError("Session operation assignment action not found")
        assign_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Назначено линий: 1" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Imported DXF LINE operation was not assigned")
        clear_buttons = [b for b in operation_window.descendants(control_type="Button")
                         if b.window_text() == "Снять назначение с DXF LINE"]
        if len(clear_buttons) != 1:
            raise RuntimeError("Session operation clear action not found")
        clear_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Назначено линий: 0" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Imported DXF LINE operation was not cleared")
        review_buttons = [b for b in operation_window.descendants(control_type="Button")
                          if b.window_text() == "Проверить назначения открытых DXF LINE"]
        if len(review_buttons) != 1:
            raise RuntimeError("GEO-3001 intent review action missing")
        review_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in operation_window.descendants(control_type="Text")]
            if any("Проверка GEO-3001" in value and "не назначено: 2" in value
                   for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("GEO-3001 did not report unresolved imported LINE")
        curve_buttons = [b for b in operation_window.descendants(control_type="Button")
                         if b.window_text() == "Предпросмотр открытой DXF-дуги/полилинии"]
        if len(curve_buttons) != 1:
            raise RuntimeError("ARC/POLYLINE independent preview action missing")
        all_combos = operation_window.descendants(control_type="ComboBox")
        if len(all_combos) < 7:
            raise RuntimeError("Open-curve DXF selector absent")
        # The open-curve selector is inserted after the processing/chain selectors.
        all_combos[-1].select(1)
        curve_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Предпросмотр кривой: DxfArc" in value and
                   "аналитическая длина" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Imported ARC was not rendered in DXF curve preview")
        operation_window.close()
        print("GUI DXF LINE/ARC selection PASS: connected LINE and curved ARC previews verified")
    finally:
        launched.terminate()
        try:
            launched.wait(timeout=5)
        except subprocess.TimeoutExpired:
            launched.kill()



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
                    probe_malformed_dxf(executable)
                    probe_open_contour_layer(executable)
                    probe_selected_dxf_line(executable)
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
