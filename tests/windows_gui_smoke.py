"""Process-bound Windows desktop smoke test for AI CutPro GUI.

GitHub hosted runners may lack an interactive desktop. An unavailable desktop
is reported, never interpreted as proof the GUI passed F1 acceptance.
"""
import pathlib
import subprocess
import sys
import time

from pywinauto import Desktop



def probe_project_picker_roundtrip(window):
    """Use actual Windows Save/Open dialogs, not synthetic app entrypoints."""
    import json
    target = (pathlib.Path.cwd() / "dist" / "a1_picker_roundtrip.json").resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    target.unlink(missing_ok=True)  # A previous run must not satisfy this test.

    def button_named(name):
        matches = [b for b in window.descendants(control_type="Button")
                   if b.window_text() == name]
        if len(matches) != 1:
            raise RuntimeError("Missing project picker action: " + name)
        return matches[0]

    def use_picker(action, submit_names, cancel=False):
        button_named(action).iface_invoke.Invoke()
        dialog = None
        for _ in range(50):
            # Match the actual file dialog by its app-supplied title, including
            # owned child windows exposed differently by Windows UIA.
            expected_title = ("Сохранить рабочий проект AI CutPro"
                              if "save" in submit_names else
                              "Открыть рабочий проект AI CutPro")
            candidates = Desktop(backend="uia").windows()
            candidates += window.descendants(control_type="Window")
            for candidate in candidates:
                if (candidate.is_visible() and candidate.handle != window.handle
                        and candidate.window_text() == expected_title
                        and candidate.process_id() == window.process_id()):
                    dialog = candidate
                    break
            if dialog is not None:
                break
            time.sleep(0.2)
        if dialog is None:
            main_labels = [t.window_text() for t in window.descendants(control_type="Text")]
            if any("Сохранение проекта отклонено" in t or
                   "Открытие проекта отклонено" in t for t in main_labels):
                raise RuntimeError("Application refused project file picker: " +
                                   repr(main_labels[-18:]))
            # GitHub-hosted Windows UIA sessions sometimes have a visible
            # app desktop but no supported native StorageProvider dialog.
            # Do not claim this picker was tested when no picker appeared.
            print("GUI NATIVE FILE PICKER NOT VERIFIED: no accessible Windows dialog for " +
                  action + "; hosted runner limitation")
            return False
        edits = dialog.descendants(control_type="Edit")
        if not edits:
            raise RuntimeError("File picker has no editable filename control: " +
                               dialog.window_text())
        # The desktop common dialog normally uses automation id 1148 for FileName.
        file_edit = next((e for e in edits
                          if e.element_info.automation_id in ("1148", "1001")
                          and e.is_visible() and e.is_enabled()), None)
        if file_edit is None:
            filename_controls = [e for e in edits if e.is_visible() and e.is_enabled()
                                 and ("file name" in e.window_text().lower()
                                      or "имя файла" in e.window_text().lower())]
            if len(filename_controls) != 1:
                raise RuntimeError("Cannot identify filename field: " + repr([
                    (e.element_info.automation_id, e.window_text()) for e in edits]))
            file_edit = filename_controls[0]
        # Native Explorer dialogs can expose a UIA Value that differs from
        # their internal filename buffer after SetValue. Use actual keyboard
        # input and commit it by leaving the field before submitting.
        file_edit.set_focus()
        file_edit.type_keys("^a", set_foreground=True)
        file_edit.type_keys(str(target), with_spaces=True, pause=0.01,
                            set_foreground=True)
        actual = file_edit.iface_value.CurrentValue
        if actual != str(target):
            raise RuntimeError("File picker filename did not update: " + repr(actual))
        print("Native picker filename verified: " + actual)
        # The Explorer common dialog can expose duplicate named buttons
        # through nested UIA providers. Submit via the focused filename
        # control's Enter key instead of guessing which button is real.
        file_edit.type_keys("{ESC}" if cancel else "{ENTER}", set_foreground=True)
        for _ in range(50):
            if not any(w.handle == dialog.handle and w.is_visible()
                       for w in Desktop(backend="uia").windows()):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Native picker did not close after keyboard submission")
        return True

    if not use_picker("Сохранить проект (.json)", {"save", "сохранить"}, cancel=True):
        raise RuntimeError("Native Save cancellation could not be verified")
    if target.exists():
        raise RuntimeError("Cancelled Save unexpectedly created the requested file")
    if not use_picker("Сохранить проект (.json)", {"save", "сохранить"}):
        return
    for _ in range(75):
        if target.is_file() and target.stat().st_size > 0:
            break
        time.sleep(0.2)
    if not target.is_file() or target.stat().st_size == 0:
        labels = [t.window_text() for t in window.descendants(control_type="Text")]
        dialogs = [(w.window_text(), w.handle) for w in Desktop(backend="uia").windows()
                   if w.process_id() == window.process_id()]
        raise RuntimeError("Windows project Save dialog did not write JSON; "
                           + repr(labels[-20:]) + "; windows=" + repr(dialogs)
                           + "; files=" + repr(list(target.parent.glob("*.json"))))
    saved = json.loads(target.read_text(encoding="utf-8-sig"))
    print("Native Save produced requested file: " + str(target))
    if saved["FormatVersion"] != 1 or not saved["Parts"]:
        raise RuntimeError("Saved project JSON is missing required fields")
    original_bytes = target.read_bytes()
    # A different valid project makes unintended adoption observable.
    alternate = target.with_name("a1_cancel_open_alternate.json")
    alternate.write_text(json.dumps({**saved, "Name": "CANCEL_OPEN_SENTINEL"}), encoding="utf-8")
    original_target = target
    target = alternate
    try:
        if not use_picker("Открыть проект (.json)", {"open", "открыть"}, cancel=True):
            raise RuntimeError("Native Open cancellation could not be verified")
        labels = [x.window_text() for x in window.descendants()]
        if any("CANCEL_OPEN_SENTINEL" in t for t in labels):
            raise RuntimeError("Cancelled Open adopted the alternate project")
        if original_target.read_bytes() != original_bytes:
            raise RuntimeError("Cancelled Open modified the saved project file")
    finally:
        target = original_target
        alternate.unlink(missing_ok=True)
    print("GUI native project cancellation PASS: Save created no file; Open did not adopt alternate project")
    if not use_picker("Открыть проект (.json)", {"open", "открыть"}):
        return
    for _ in range(30):
        labels = [x.window_text() for x in window.descendants(control_type="Text")]
        if any("открыт:" in t and "детал" in t for t in labels):
            print("GUI native project Save/Open pickers PASS: real JSON roundtrip")
            return
        time.sleep(0.2)
    raise RuntimeError("Windows Open dialog did not restore project: " + repr(labels))


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

    for action in ("Применить параметры проекта", "Создать новый проект",
                   "Открыть проект (.json)", "Сохранить проект (.json)"):
        button_named(action)
    invoke(button_named("Применить параметры проекта"))
    assert_status("Параметры проекта применены.")
    invoke(button_named("Создать новый проект"))
    assert_status("Создан новый редактируемый проект.")
    print("GUI project controls PASS: apply/create are live, open/save are present")
    invoke(button_named("Добавить деталь в проект"))
    assert_status("Добавлена деталь №2. Всего: 2.")
    invoke(button_named("Применить параметры проекта"))
    assert_status("Параметры проекта применены.")
    invoke(button_named("Удалить выбранную деталь"))
    assert_status("Деталь удалена. Осталось: 1.")
    if not [c for c in window.descendants(control_type="ComboBox")
            if c.window_text() == "1. Кронштейн × 10"]:
        # Avalonia UIA sometimes exposes the selection on a child Text node.
        if not any("1. Кронштейн × 10" in t for t in ui_text()):
            raise RuntimeError("Part selector failed to return to one project part")
    print("GUI multi-part editor PASS: add/apply/remove operations and selector")
    probe_project_picker_roundtrip(window)

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
        "0\nLINE\n8\nCURVES\n10\n80\n20\n50\n11\n110\n21\n50\n"
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
            if any("Проверка GEO-3001" in value and "не назначено: 3" in value
                   for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("GEO-3001 did not report unresolved imported LINE")
        curve_buttons = [b for b in operation_window.descendants(control_type="Button")
                         if b.window_text() == "Предпросмотр открытой DXF-дуги/полилинии"]
        if len(curve_buttons) != 1:
            raise RuntimeError("ARC/POLYLINE independent preview action missing")
        pick_curve = [b for b in operation_window.descendants(control_type="Button")
                      if b.window_text() == "Выбрать первую импортированную DXF-кривую"]
        if len(pick_curve) != 1:
            raise RuntimeError("Explicit imported curve selection button missing")
        pick_curve[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Предпросмотр кривой: DxfArc" in value and
                   "аналитическая длина" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError(
                "Imported ARC was not rendered in DXF curve preview; "
                + "labels=" + repr(labels)
                + "; buttons=" + repr([b.window_text() for b in curve_buttons]))
        mixed_buttons = [b for b in operation_window.descendants(control_type="Button")
                         if b.window_text() ==
                         "Предпросмотр смешанной цепочки LINE/ARC/POLYLINE"]
        if len(mixed_buttons) != 1:
            raise RuntimeError("Mixed LINE/ARC preview action missing")
        mixed_buttons[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Смешанная цепочка: 2 DXF-объектов" in value
                   and "Аналитические дуги сохранены" in value for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Connected imported ARC+LINE was not previewed: " + repr(labels))
        mixed_assign = [b for b in operation_window.descendants(control_type="Button")
                        if b.window_text() ==
                        "Назначить смешанную цепочку (предпросмотр)"]
        mixed_remove = [b for b in operation_window.descendants(control_type="Button")
                        if b.window_text() ==
                        "Снять назначение смешанной цепочки"]
        mixed_save = [b for b in operation_window.descendants(control_type="Button")
                      if b.window_text() ==
                      "Сохранить смешанные назначения (.json)"]
        mixed_load = [b for b in operation_window.descendants(control_type="Button")
                      if b.window_text() ==
                      "Загрузить смешанные назначения (.json)"]
        if not (len(mixed_assign) == len(mixed_remove) ==
                len(mixed_save) == len(mixed_load) == 1):
            raise RuntimeError("Mixed annotation assignment and JSON actions missing")
        mixed_assign[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Назначена смешанная цепочка. Всего: 1" in value
                   for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Imported ARC/LINE mixed assignment failed: " + repr(labels))
        mixed_remove[0].iface_invoke.Invoke()
        for _ in range(15):
            labels = [x.window_text() for x in
                      operation_window.descendants(control_type="Text")]
            if any("Смешанных цепочек осталось: 0" in value
                   for value in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Mixed annotation removal failed: " + repr(labels))
        operation_window.close()
        print("GUI mixed ARC+LINE preview and assignment/removal PASS")
    finally:
        launched.terminate()
        try:
            launched.wait(timeout=5)
        except subprocess.TimeoutExpired:
            launched.kill()



def probe_project_file(executable):
    """Reopen an actual v1 JSON project through the same model as the UI."""
    import json
    fixture = pathlib.Path.cwd() / "dist" / "gui_project_fixture.json"
    fixture.parent.mkdir(parents=True, exist_ok=True)
    fixture.write_text(json.dumps({
        "FormatVersion": 1, "Name": "GUI_PROJECT_ROUNDTRIP",
        "Sheet": {"WidthMm": 800, "HeightMm": 500},
        "Parts": [{"Name": "GUI_BRACKET", "WidthMm": 80,
                   "HeightMm": 50, "Quantity": 3}],
        "Material": "Steel", "ThicknessMm": 2.5
    }), encoding="utf-8")
    proc = subprocess.Popen([str(executable), "--open-project", str(fixture)],
                            cwd=executable.parent)
    try:
        project_window = None
        for _ in range(30):
            if proc.poll() is not None:
                raise RuntimeError("Project fixture process exited early")
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == proc.pid and
                       "AI CutPro" in w.window_text()]
            if matches:
                project_window = matches[0]
                break
            time.sleep(0.25)
        if project_window is None:
            raise RuntimeError("Reopened project window did not appear")
        for _ in range(20):
            labels = [x.window_text() for x in
                      project_window.descendants(control_type="Text")]
            if any("GUI_PROJECT_ROUNDTRIP" in t and
                   "800" in t and "500" in t for t in labels):
                break
            time.sleep(0.3)
        else:
            raise RuntimeError("Real project JSON did not reopen in GUI: " +
                               repr(labels[-10:]))
        print("GUI project file PASS: parsed real JSON and reopened dimensions")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


def probe_embedded_project_file(executable):
    """Real project JSON with native DXF and technology is reopened by Avalonia."""
    fixture = pathlib.Path.cwd() / "external-dxf-fixtures" / "embedded_gui_project.json"
    if not fixture.is_file():
        raise RuntimeError("Verified core project/embedded-DXF fixture was not generated")
    proc = subprocess.Popen([str(executable), "--open-project", str(fixture)],
                            cwd=executable.parent)
    try:
        window = None
        for _ in range(30):
            if proc.poll() is not None:
                raise RuntimeError("Embedded DXF project process exited before UI")
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == proc.pid and
                       "AI CutPro" in w.window_text()]
            if matches:
                window = matches[0]
                break
            time.sleep(0.25)
        if window is None:
            raise RuntimeError("Embedded project window did not appear")
        for _ in range(20):
            labels = [w.window_text() for w in
                      window.descendants(control_type="Text")]
            if any("DXF technology project" in t and
                   "200" in t and "130" in t for t in labels):
                break
            time.sleep(0.3)
        else:
            raise RuntimeError("Embedded DXF project was not reopened: " + repr(labels[-12:]))
        replacement_actions = [b for b in window.descendants(control_type="Button")
                               if b.window_text() ==
                               "Заменить DXF проекта (сброс назначений)"]
        if len(replacement_actions) != 1 or not replacement_actions[0].is_visible():
            raise RuntimeError(
                "Embedded DXF source replacement must be visibly available "
                "with an explicit reset-assignments label")
        print("GUI DXF replacement entry PASS: explicit reset action visible; "
              "native picker and confirmation still NOT VERIFIED on hosted runner")
        buttons = [b for b in window.descendants(control_type="Button")
                   if b.window_text() == "Технологические линии (предпросмотр)"]
        if len(buttons) != 1:
            raise RuntimeError("Embedded DXF editor button missing")
        buttons[0].iface_invoke.Invoke()
        editor = None
        for _ in range(20):
            matches = [w for w in Desktop(backend="uia").windows()
                       if w.process_id() == proc.pid and
                       "Технологические линии" in w.window_text()]
            if matches:
                editor = matches[0]
                break
            time.sleep(0.25)
        if editor is None:
            raise RuntimeError("Embedded DXF technology editor did not reopen")
        labels = [w.window_text() for w in editor.descendants(control_type="Text")]
        if not any("смешанных 1" in value and "Из проекта восстановлено" in value
                   for value in labels):
            raise RuntimeError("Saved ARC technology was not restored: " + repr(labels))
        editor.close()
        for _ in range(30):
            main_labels = [t.window_text() for t in
                           window.descendants(control_type="Text")]
            if any("Назначения DXF сохранены в текущем проекте" in t
                   for t in main_labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Technology editor did not finish closing: " +
                               repr(main_labels[-12:]))
        print("GUI embedded DXF project PASS: original geometry and ARC annotation restored")
        new_buttons = [b for b in window.descendants(control_type="Button")
                       if b.window_text() == "Создать новый проект"]
        if len(new_buttons) != 1:
            raise RuntimeError("New project action absent in embedded DXF session")
        new_buttons[0].iface_invoke.Invoke()
        cancel = None
        for _ in range(30):
            choices = [b for b in window.descendants(control_type="Button")
                       if b.window_text() == "Отменить смену проекта"
                       and b.is_visible()]
            if choices:
                cancel = choices[0]
                break
            time.sleep(0.2)
        if cancel is None:
            labels = [t.window_text() for t in window.descendants(control_type="Text")]
            raise RuntimeError("Inline project replacement confirmation missing: " +
                               repr(labels[-14:]))
        cancel.iface_invoke.Invoke()
        for _ in range(20):
            labels = [t.window_text() for t in window.descendants(control_type="Text")]
            if any("Смена проекта отменена" in t for t in labels):
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("Project cancellation was not acknowledged")
        # The DXF preview remains active after cancel; use its own details,
        # not incidental Edit control text that UIA backends may omit.
        after_cancel = [t.window_text() for t in window.descendants(control_type="Text")]
        if not any("DXF: щёлкните по прямой LINE" in t for t in after_cancel):
            raise RuntimeError("Cancelling replacement unexpectedly removed DXF geometry")
        print("GUI project safety PASS: cancel preserves embedded DXF state")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


def probe_embedded_direct_edit(executable):
    """Exercise cancellation, actual geometry edit and joint history in hosted UI."""
    fixture = pathlib.Path.cwd() / "external-dxf-fixtures" / "embedded_gui_edit.json"
    if not fixture.is_file():
        raise RuntimeError("Closed embedded edit fixture absent")
    proc = subprocess.Popen([str(executable), "--open-project", str(fixture)],
                            cwd=executable.parent)
    def wait_for(find, description):
        for _ in range(40):
            value = find()
            if value:
                return value
            time.sleep(0.2)
        raise RuntimeError("Embedded edit GUI: " + description)
    def button(window, label):
        return next((b for b in window.descendants(control_type="Button")
                     if b.window_text() == label), None)
    def status(window, fragment):
        return wait_for(lambda: any(fragment in t.window_text()
                        for t in window.descendants(control_type="Text")), fragment)
    try:
        window = wait_for(lambda: next((w for w in Desktop(backend="uia").windows()
                          if w.process_id() == proc.pid and
                          "AI CutPro" in w.window_text()), None), "main window")
        label = "Добавить круглое отверстие (предпросмотр)"
        def edit_dialog():
            wait_for(lambda: button(window, label), label).iface_invoke.Invoke()
            def find_confirmation():
                # Owned Avalonia dialogs may be exposed as child windows, not
                # top-level desktop windows on hosted Windows UIA.
                candidates = [w for w in Desktop(backend="uia").windows()
                              if w.process_id() == proc.pid]
                candidates += window.descendants(control_type="Window")
                for candidate in candidates:
                    if button(candidate, "Применить и сбросить назначения"):
                        return candidate
                return None
            try:
                return wait_for(find_confirmation, "edit confirmation")
            except RuntimeError as exc:
                labels = [t.window_text() for t in window.descendants(control_type="Text")]
                titles = [w.window_text() for w in Desktop(backend="uia").windows()
                          if w.process_id() == proc.pid]
                raise RuntimeError("Edit confirmation absent; application status="
                                   + repr(labels[-30:]) + "; windows=" + repr(titles)) from exc
        dialog = edit_dialog()
        wait_for(lambda: button(dialog, "Отмена"), "cancel").iface_invoke.Invoke()
        status(window, "Правка отменена")
        status(window, "Круговых элементов нет.")
        wait_for(lambda: button(window, "Отменить изменение"), "undo").iface_invoke.Invoke()
        status(window, "Нет изменений DXF для отмены")
        dialog = edit_dialog()
        wait_for(lambda: button(dialog, "Применить и сбросить назначения"),
                 "confirm").iface_invoke.Invoke()
        status(window, "Новое отверстие")
        status(window, "Ø 20 мм: 1 шт.")
        button(window, "Отменить изменение").iface_invoke.Invoke()
        status(window, "Изменение отменено")
        status(window, "Круговых элементов нет.")
        button(window, "Повторить изменение").iface_invoke.Invoke()
        status(window, "Изменение повторено")
        status(window, "Ø 20 мм: 1 шт.")
        print("GUI embedded direct edit PASS: cancel, circle edit, Undo/Redo")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


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
                    probe_project_file(executable)
                    probe_embedded_project_file(executable)
                    probe_embedded_direct_edit(executable)
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
