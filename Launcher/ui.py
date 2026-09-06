import os
import sys
import json
import launcher_core
from PySide6.QtCore import QObject, QRunnable, QThreadPool, Signal, Qt, QPoint, QTimer, QEvent
from PySide6.QtGui import QFontDatabase, QFont, QPixmap, QColor
from PySide6.QtWidgets import (
    QApplication, QLabel, QMainWindow, QMessageBox,
    QPushButton, QMenu, QWidget, QProgressBar,
    QDialog, QLineEdit, QComboBox, QFileDialog,
    QHBoxLayout, QVBoxLayout, QGridLayout, QCheckBox,
    QSlider, QGraphicsDropShadowEffect, QFrame, QSpacerItem,
    QSizePolicy, QScrollArea
)


class WorkerSignals(QObject):
    status_updated = Signal(str)
    finished = Signal(int)

    def __init__(self):
        super().__init__()


class VerifyRunnable(QRunnable):
    def __init__(self):
        super().__init__()
        self.signals = WorkerSignals()
        self.setAutoDelete(True)

    def run(self):
        try:
            props = launcher_core.load_properties()
            version = props.get("minecraft.version", "")
            installed = launcher_core.get_installed_versions()
            
            if version and version not in installed:
                # Si no esta instalada, la descargamos de Mojang
                self.signals.status_updated.emit("Iniciando descarga oficial...")
                launcher_core.install_vanilla_version(version, self.signals.status_updated.emit)
                result = 0
            else:
                # Verificación normal del modpack / launcher original
                result = launcher_core.verify_client(self.signals.status_updated.emit)
        except Exception as ex:
            self.signals.status_updated.emit(f"Error de verificacion: {ex}")
            result = -1
        self.signals.finished.emit(result)


class LaunchRunnable(QRunnable):
    def __init__(self):
        super().__init__()
        self.signals = WorkerSignals()
        self.setAutoDelete(True)

    def run(self):
        try:
            result = launcher_core.launch_game()
        except Exception as ex:
            self.signals.status_updated.emit(f"Error al iniciar el juego: {ex}")
            result = -1
        self.signals.finished.emit(result)


class VersionFetchSignals(QObject):
    version_ready = Signal(str)

class VersionFetchRunnable(QRunnable):
    """Obtiene la version del cliente desde el servidor en segundo plano."""
    def __init__(self):
        super().__init__()
        self.signals = VersionFetchSignals()
        self.setAutoDelete(True)

    def run(self):
        version = launcher_core.get_client_version()
        if version:
            self.signals.version_ready.emit(version)


class DraggableWindow(QMainWindow):
    # Ventana sin marcos del sistema, arrastrable con click-drag

    def __init__(self):
        super().__init__()
        self._drag_pos = None
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.Window)
        self.setAttribute(Qt.WA_TranslucentBackground)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()

    def mouseMoveEvent(self, event):
        if event.buttons() == Qt.LeftButton and self._drag_pos is not None:
            self.move(event.globalPosition().toPoint() - self._drag_pos)

    def mouseReleaseEvent(self, event):
        self._drag_pos = None


def _make_wc_button(symbol, parent):
    # Boton de control de ventana minimalista
    btn = QPushButton(symbol, parent)
    btn.setFixedSize(32, 32)
    btn.setCursor(Qt.PointingHandCursor)
    btn.setFont(QFont("Boring Time", 10, QFont.Bold))
    btn.setStyleSheet(
        f"QPushButton {{"
        f"  background-color: transparent;"
        f"  color: #ffffff;"
        f"  border: 1px solid transparent;"
        f"  border-radius: 8px;"
        f"}}"
        f"QPushButton:hover {{"
        f"  background-color: rgba(255,255,255,40);"
        f"  border: 1px solid rgba(255,255,255,80);"
        f"}}"
    )
    return btn



class SettingsDialog(QDialog):
    """Panel de opciones del launcher — estilo moderno glassmorphism oscuro."""

    # Tamaño del panel (centrado sobre la ventana principal 1275x700)
    W = 560
    H = 380

    def __init__(self, parent=None, font_family="Boring Time"):
        super().__init__(parent)
        self.font_family = font_family
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.Dialog)
        self.setAttribute(Qt.WA_TranslucentBackground)
        
        if parent:
            pg = parent.geometry()
            self.setFixedSize(pg.width(), pg.height())
            self.move(pg.x(), pg.y())
        else:
            self.setFixedSize(1275, 700)

        self._build()

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            self._drag_pos = event.globalPosition().toPoint()
            if self.parent():
                self._parent_pos = self.parent().frameGeometry().topLeft()
            self._dialog_pos = self.frameGeometry().topLeft()

    def mouseMoveEvent(self, event):
        if event.buttons() == Qt.LeftButton and getattr(self, "_drag_pos", None) is not None:
            delta = event.globalPosition().toPoint() - self._drag_pos
            if self.parent() and getattr(self, "_parent_pos", None) is not None:
                self.parent().move(self._parent_pos + delta)
            self.move(self._dialog_pos + delta)

    def mouseReleaseEvent(self, event):
        self._drag_pos = None

    # ── Helpers de estilo ─────────────────────────────────────────────
    def _make_label(self, text):
        """Crea un label con estilo: blanco, negrita, mayúsculas, 10px."""
        lbl = QLabel(text.upper())
        lbl.setFont(QFont(self.font_family, 10, QFont.Bold))
        lbl.setStyleSheet("color: #ffffff; background: transparent;")
        return lbl

    def _make_input(self):
        """Crea un QLineEdit oscuro con bordes sutiles."""
        edit = QLineEdit()
        edit.setFont(QFont(self.font_family, 10))
        edit.setStyleSheet(
            "QLineEdit {"
            "  background-color: rgba(15, 18, 22, 180);"
            "  color: #ffffff;"
            "  border: 1px solid rgba(255, 255, 255, 20);"
            "  border-radius: 6px;"
            "  padding: 6px 10px;"
            "}"
            "QLineEdit:focus {"
            "  border: 1px solid rgba(255, 255, 255, 60);"
            "}"
        )
        return edit

    def _make_combo(self):
        """Crea un QComboBox oscuro con bordes sutiles."""
        combo = QComboBox()
        combo.setFont(QFont(self.font_family, 10))
        combo.setStyleSheet(
            "QComboBox {"
            "  background-color: rgba(15, 18, 22, 180);"
            "  color: #ffffff;"
            "  border: 1px solid rgba(255, 255, 255, 20);"
            "  border-radius: 6px;"
            "  padding: 6px 10px;"
            "}"
            "QComboBox:focus {"
            "  border: 1px solid rgba(255, 255, 255, 60);"
            "}"
            "QComboBox::drop-down { border: none; width: 20px; }"
            "QComboBox::down-arrow { image: none; }"
            "QComboBox QAbstractItemView {"
            "  background: rgba(25, 30, 38, 240);"
            "  color: #ffffff;"
            "  border: 1px solid rgba(255, 255, 255, 30);"
            "  border-radius: 6px;"
            "  selection-background-color: rgba(39, 174, 96, 120);"
            "  padding: 4px;"
            "}"
        )
        return combo

    # ── Construcción del panel ────────────────────────────────────────
    def _build(self):
        props = launcher_core.load_properties()

        # Fondo casi transparente para capturar eventos de mouse en Windows
        backdrop = QFrame(self)
        backdrop.setGeometry(0, 0, self.width(), self.height())
        backdrop.setStyleSheet("background-color: rgba(0, 0, 0, 0.01);")

        # ── Panel contenedor (glassmorphism oscuro) ──────────────────
        panel = QFrame(self)
        px = (self.width() - self.W) // 2
        py = (self.height() - self.H) // 2
        panel.setGeometry(px, py, self.W, self.H)
        panel.setStyleSheet(
            "QFrame {"
            "  background-color: rgba(35, 40, 48, 230);"
            "  border: 0px solid rgba(255, 255, 255, 8);"
            "  border-radius: 12px;"
            "}"
        )

        # Sombra sutil para que el panel resalte sobre el fondo
        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(40)
        shadow.setOffset(0, 8)
        shadow.setColor(QColor(0, 0, 0, 120))
        panel.setGraphicsEffect(shadow)

        # ── Layout principal del panel ───────────────────────────────
        main_layout = QVBoxLayout(panel)
        main_layout.setContentsMargins(24, 16, 24, 20)
        main_layout.setSpacing(6)

        # — Cabecera (título + botón cerrar) —
        header_layout = QHBoxLayout()
        title = QLabel("OPCIONES")
        title.setFont(QFont(self.font_family, 15, QFont.Bold))
        title.setStyleSheet("color: #ffffff; background: transparent;")
        header_layout.addWidget(title)
        header_layout.addStretch()

        close_btn = QPushButton("✕")
        close_btn.setFixedSize(28, 28)
        close_btn.setFont(QFont(self.font_family, 10, QFont.Bold))
        close_btn.setCursor(Qt.PointingHandCursor)
        close_btn.setStyleSheet(
            "QPushButton { background: transparent; color: rgba(255,255,255,150); border: none; border-radius: 6px; }"
            "QPushButton:hover { background: rgba(255,80,80,60); color: #ff5555; }"
        )
        close_btn.clicked.connect(self.reject)
        header_layout.addWidget(close_btn)
        main_layout.addLayout(header_layout)

        # — Separador —
        sep = QFrame()
        sep.setFixedHeight(1)
        sep.setStyleSheet("background: rgba(255, 255, 255, 40);")
        main_layout.addWidget(sep)
        main_layout.addSpacing(8)

        # ── Área scrollable para opciones ────────────────────────────────
        scroll_content = QWidget()
        scroll_content.setStyleSheet("background: transparent;")
        content_layout = QVBoxLayout(scroll_content)
        content_layout.setContentsMargins(0, 0, 8, 0)
        content_layout.setSpacing(6)

        # ── Grid de opciones ─────────────────────────────────────────
        grid = QGridLayout()
        grid.setHorizontalSpacing(16)
        grid.setVerticalSpacing(6)

        # Fila 0: Labels RAM y Nombre de Jugador (con icono info)
        grid.addWidget(self._make_label("MEMORIA RAM"), 0, 0)
        name_label = self._make_label("NOMBRE DE JUGADOR")
        info_btn = QPushButton("i")
        info_btn.setFixedSize(14, 14)
        info_btn.setFont(QFont("Arial", 7, QFont.Bold))
        info_btn.setCursor(Qt.PointingHandCursor)
        info_btn.setStyleSheet(
            "QPushButton {"
            "  background: transparent;"
            "  color: rgba(255,255,255,140);"
            "  border: 1px solid rgba(255,255,255,100);"
            "  border-radius: 7px;"
            "  padding: 0px;"
            "}"
            "QPushButton:hover {"
            "  color: rgba(255,255,255,220);"
            "  border-color: rgba(255,255,255,180);"
            "}"
        )

        # Tooltip personalizado como QLabel flotante (los OS tooltips no funcionan con WA_TranslucentBackground)
        self._info_popup = QLabel(
            "• De 4 a 16 caracteres\n"
            "• Solo letras, números y guion bajo ( _ )\n"
            "• Sin espacios ni símbolos especiales",
            panel
        )
        self._info_popup.setFont(QFont("Arial", 8))
        self._info_popup.setStyleSheet(
            "QLabel {"
            "  background: rgba(30, 35, 42, 240);"
            "  color: rgba(255,255,255,200);"
            "  border: 1px solid rgba(255,255,255,30);"
            "  border-radius: 6px;"
            "  padding: 8px 10px;"
            "}"
        )
        self._info_popup.setWordWrap(True)
        self._info_popup.adjustSize()
        self._info_popup.hide()
        self._info_popup.setAttribute(Qt.WA_TransparentForMouseEvents)

        # Hover con retardo de 100ms usando event filter
        _show_timer = QTimer()
        _show_timer.setSingleShot(True)
        _show_timer.setInterval(150)

        def _show_popup():
            btn_pos = info_btn.mapTo(panel, info_btn.rect().bottomLeft())
            popup_x = min(btn_pos.x(), panel.width() - self._info_popup.width() - 8)
            self._info_popup.move(popup_x, btn_pos.y() + 4)
            self._info_popup.raise_()
            self._info_popup.show()

        _show_timer.timeout.connect(_show_popup)

        class _InfoHover(QObject):
            def eventFilter(self_, obj, event):
                if event.type() == QEvent.Enter:
                    _show_timer.start()
                elif event.type() == QEvent.Leave:
                    _show_timer.stop()
                    self._info_popup.hide()
                return False

        _hover_filter = _InfoHover(info_btn)
        info_btn.installEventFilter(_hover_filter)
        # Guardar referencia para que no sea recolectado por el GC
        info_btn._hover_filter = _hover_filter
        info_btn._show_timer = _show_timer

        name_header = QHBoxLayout()
        name_header.setContentsMargins(0, 0, 0, 0)
        name_header.setSpacing(6)
        name_header.addWidget(name_label)
        name_header.addWidget(info_btn)
        name_header.addStretch()
        grid.addLayout(name_header, 0, 1)

        # Fila 1: Inputs RAM y Nombre
        self.ram_combo = self._make_combo()
        # Mostrar "X GB" en la UI pero guardar "XG" para la JVM
        for val in ["1G","2G","3G","4G","6G","8G","10G","12G","16G"]:
            self.ram_combo.addItem(val.replace("G", " GB"), val)
        current_ram = props.get("java.memory", "4G")
        for i in range(self.ram_combo.count()):
            if self.ram_combo.itemData(i) == current_ram:
                self.ram_combo.setCurrentIndex(i)
                break
        grid.addWidget(self.ram_combo, 1, 0)

        self.user_edit = self._make_input()
        self.user_edit.setText(props.get("player.username", ""))
        self.user_edit.setPlaceholderText("Nombre de jugador...")
        self.user_edit.setMaxLength(16)
        grid.addWidget(self.user_edit, 1, 1)

        # Error label debajo del nombre (fila 2 col 1)
        self.user_error = QLabel("")
        self.user_error.setFont(QFont("Arial", 8))
        self.user_error.setStyleSheet("color: #ff4444; background: transparent;")
        self.user_error.setWordWrap(True)
        self.user_error.hide()
        grid.addWidget(self.user_error, 2, 1)

        # Validación en tiempo real
        self.user_edit.textChanged.connect(self._validate_username)

        # Fila 3: Label Ruta de Java
        grid.addWidget(self._make_label("RUTA DE JAVA"), 3, 0, 1, 2)

        # Fila 4: Input Java + botón "..."
        java_row = QHBoxLayout()
        java_row.setSpacing(6)
        self.java_edit = self._make_input()
        self.java_edit.setText(props.get("java.path", ""))
        self.java_edit.setPlaceholderText("C:\\ruta\\al\\java.exe")
        java_row.addWidget(self.java_edit)

        browse_btn = QPushButton("...")
        browse_btn.setFixedSize(36, 36)
        browse_btn.setFont(QFont(self.font_family, 11, QFont.Bold))
        browse_btn.setCursor(Qt.PointingHandCursor)
        browse_btn.setStyleSheet(
            "QPushButton {"
            "  background: rgba(80, 85, 95, 200);"
            "  color: #ffffff;"
            "  border: none;"
            "  border-radius: 6px;"
            "}"
            "QPushButton:hover {"
            "  background: rgba(100, 105, 115, 230);"
            "}"
        )
        browse_btn.clicked.connect(self._browse_java)
        java_row.addWidget(browse_btn)
        grid.addLayout(java_row, 4, 0, 1, 2)

        # Fila 5: Label URL
        grid.addWidget(self._make_label("URL DEL SERVIDOR"), 5, 0)

        # Fila 6: Input URL (izquierda) + Checkbox (derecha)
        self.srv_edit = self._make_input()
        self.srv_edit.setText(props.get("server.url", ""))
        self.srv_edit.setPlaceholderText("http://servidor:puerto")
        grid.addWidget(self.srv_edit, 6, 0)

        self.verify_check = QCheckBox("VERIFICAR ARCHIVOS AL INICIAR")
        self.verify_check.setFont(QFont(self.font_family, 9, QFont.Bold))
        self.verify_check.setStyleSheet(
            "QCheckBox { color: rgba(255,255,255,200); background: transparent; spacing: 8px; }"
            "QCheckBox::indicator {"
            "  width: 16px; height: 16px;"
            "  border: 0px solid rgba(255,255,255,60);"
            "  border-radius: 4px;"
            "  background: rgba(15, 18, 22, 180);"
            "}"
            "QCheckBox::indicator:hover {"
            "  border-color: rgba(255,255,255,100);"
            "}"
            "QCheckBox::indicator:checked {"
            "  background: #27AE60;"
            "  border-color: #2ECC71;"
            "}"
        )
        self.verify_check.setChecked(props.get("enable.verify", "true").lower() == "true")
        grid.addWidget(self.verify_check, 6, 1)

        # Fila 7: Label Carpeta de Minecraft
        grid.addWidget(self._make_label("CARPETA DE MINECRAFT"), 7, 0, 1, 2)

        # Fila 8: Input ruta + botón "..."
        game_row = QHBoxLayout()
        game_row.setSpacing(6)
        self.game_path_edit = self._make_input()
        self.game_path_edit.setText(props.get("game.path", "../Minecraft"))
        self.game_path_edit.setPlaceholderText("Ruta a la carpeta del juego...")
        game_row.addWidget(self.game_path_edit)

        browse_game_btn = QPushButton("...")
        browse_game_btn.setFixedSize(36, 36)
        browse_game_btn.setFont(QFont(self.font_family, 11, QFont.Bold))
        browse_game_btn.setCursor(Qt.PointingHandCursor)
        browse_game_btn.setStyleSheet(
            "QPushButton {"
            "  background: rgba(80, 85, 95, 200);"
            "  color: #ffffff;"
            "  border: none;"
            "  border-radius: 6px;"
            "}"
            "QPushButton:hover {"
            "  background: rgba(100, 105, 115, 230);"
            "}"
        )
        browse_game_btn.clicked.connect(self._browse_game_path)
        game_row.addWidget(browse_game_btn)
        grid.addLayout(game_row, 8, 0, 1, 2)

        content_layout.addLayout(grid)
        content_layout.addStretch()

        # ── Scroll Area con scrollbar estilizado ─────────────────────
        scroll = QScrollArea()
        scroll.setWidget(scroll_content)
        scroll.setWidgetResizable(True)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        scroll.setStyleSheet(
            "QScrollArea { background: transparent; border: none; }"
            "QScrollBar:vertical {"
            "  background: transparent;"
            "  width: 6px;"
            "  margin: 4px 0;"
            "  border-radius: 3px;"
            "}"
            "QScrollBar::handle:vertical {"
            "  background: rgba(200, 210, 220, 120);"
            "  min-height: 30px;"
            "  border-radius: 3px;"
            "}"
            "QScrollBar::handle:vertical:hover {"
            "  background: rgba(230, 235, 240, 180);"
            "}"
            "QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {"
            "  height: 0; background: none;"
            "}"
            "QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {"
            "  background: none;"
            "}"
        )
        main_layout.addWidget(scroll)

        # ── Botones Guardar / Cancelar (alineados a la derecha) ──────
        btn_layout = QHBoxLayout()
        btn_layout.addStretch()

        save_btn = QPushButton("GUARDAR")
        save_btn.setFixedSize(110, 36)
        save_btn.setFont(QFont(self.font_family, 10, QFont.Bold))
        save_btn.setCursor(Qt.PointingHandCursor)
        save_btn.setStyleSheet(
            "QPushButton {"
            "  background-color: #27AE60;"
            "  color: #ffffff;"
            "  border: none;"
            "  border-radius: 6px;"
            "}"
            "QPushButton:hover {"
            "  background-color: #2ECC71;"
            "}"
            "QPushButton:pressed {"
            "  background-color: #1E8449;"
            "}"
        )
        save_btn.clicked.connect(self._save)
        btn_layout.addWidget(save_btn)

        cancel_btn = QPushButton("CANCELAR")
        cancel_btn.setFixedSize(110, 36)
        cancel_btn.setFont(QFont(self.font_family, 10, QFont.Bold))
        cancel_btn.setCursor(Qt.PointingHandCursor)
        cancel_btn.setStyleSheet(
            "QPushButton {"
            "  background: rgba(80, 85, 95, 200);"
            "  color: #ffffff;"
            "  border: none;"
            "  border-radius: 6px;"
            "}"
            "QPushButton:hover {"
            "  background: rgba(100, 105, 115, 230);"
            "}"
        )
        cancel_btn.clicked.connect(self.reject)
        btn_layout.addWidget(cancel_btn)

        main_layout.addLayout(btn_layout)

    def _validate_username(self, text):
        import re
        text = text.strip()
        if not text:
            self.user_error.setText("el nombre no puede estar vacío")
            self.user_error.show()
        elif len(text) < 4:
            self.user_error.setText("el nombre debe contener mínimo 4 caracteres")
            self.user_error.show()
        elif len(text) > 16:
            self.user_error.setText("el nombre no puede superar 16 caracteres")
            self.user_error.show()
        elif not re.match(r'^[A-Za-z0-9_]+$', text):
            self.user_error.setText("solo letras, números y guion bajo ( _ )")
            self.user_error.show()
        else:
            self.user_error.hide()

    def _browse_java(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Seleccionar Java", "", "Ejecutables (*.exe);;Todos (*)"
        )
        if path:
            self.java_edit.setText(path)

    def _browse_game_path(self):
        folder = QFileDialog.getExistingDirectory(
            self, "Seleccionar carpeta de Minecraft"
        )
        if folder:
            self.game_path_edit.setText(folder)

    def _save(self):
        import re
        username = self.user_edit.text().strip()
        if not re.match(r'^[A-Za-z0-9_]{4,16}$', username):
            self.user_error.show()
            self.user_edit.setFocus()
            return

        # --- launcher.properties ---
        launcher_core.save_property("player.username", username)
        launcher_core.save_property("java.memory",     self.ram_combo.currentData())
        launcher_core.save_property("java.path",       self.java_edit.text().strip())
        launcher_core.save_property("server.url",      self.srv_edit.text().strip())
        launcher_core.save_property("game.path",       self.game_path_edit.text().strip())
        launcher_core.save_property("enable.verify",   "true" if self.verify_check.isChecked() else "false")

        self.accept()


class LauncherUI:
    WINDOW_WIDTH = 1275
    WINDOW_HEIGHT = 700

    def __init__(self):
        self.app = QApplication.instance() or QApplication(sys.argv)
        self.window = DraggableWindow()
        self.window.setWindowTitle("Fkonnor Launcher")
        self.window.setFixedSize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)

        self.central_widget = QWidget()
        self.window.setCentralWidget(self.central_widget)

        self._load_fonts()
        self._build_ui()
        self._apply_styles()
        self._start_version_fetch()

    def _start_version_fetch(self):
        class FetchSignals(QObject):
            done = Signal()
        self.fetch_signals = FetchSignals()
        self.fetch_signals.done.connect(self._on_fetch_done)
        
        class Fetch(QRunnable):
            def __init__(self, signals):
                super().__init__()
                self.signals = signals
            def run(self_run):
                launcher_core.fetch_mojang_versions_sync()
                self_run.signals.done.emit()
                
        QThreadPool.globalInstance().start(Fetch(self.fetch_signals))

    def _on_fetch_done(self):
        if hasattr(self, 'version_popup') and self.version_popup.isVisible():
            self.version_popup.hide()
            self._show_version_menu_above()

    def _load_fonts(self):
        self.font_family = "Arial"
        font_path = os.path.join(os.path.dirname(__file__), "Boring Time.otf")
        if os.path.isfile(font_path):
            font_id = QFontDatabase.addApplicationFont(font_path)
            if font_id != -1:
                families = QFontDatabase.applicationFontFamilies(font_id)
                if families:
                    self.font_family = families[0]

    def _build_ui(self):
        W = self.WINDOW_WIDTH
        H = self.WINDOW_HEIGHT

        # FONDO
        self.background_label = QLabel(self.central_widget)
        self.background_label.setGeometry(0, 0, W, H)
        self.background_label.setScaledContents(True)

        image_path = os.path.join(os.path.dirname(__file__), "background.png")
        if os.path.isfile(image_path):
            pixmap = QPixmap(image_path)
            if not pixmap.isNull():
                self.background_label.setPixmap(
                    pixmap.scaled(W, H, Qt.KeepAspectRatioByExpanding, Qt.SmoothTransformation)
                )
            else:
                self.background_label.setStyleSheet(
                    "background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d1c2e, stop:1 #0a1220);"
                )
        else:
            self.background_label.setStyleSheet(
                "background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d1c2e, stop:1 #0a1220);"
            )   

        # BOTONES DE CONTROL DE VENTANA (superior derecha)
        self.btn_close = _make_wc_button("✕", self.central_widget)
        self.btn_close.setGeometry(W - 40, 10, 32, 32)
        self.btn_close.clicked.connect(self.window.close)
        self.btn_close.raise_()

        self.btn_minimize = _make_wc_button("—", self.central_widget)
        self.btn_minimize.setGeometry(W - 76, 10, 32, 32)
        self.btn_minimize.clicked.connect(self.window.showMinimized)
        self.btn_minimize.raise_()

        # BOTON OPCIONES (superior izquierda)
        self.btn_settings = QPushButton("⚙  Opciones", self.central_widget)
        self.btn_settings.setGeometry(12, 10, 110, 32)
        self.btn_settings.setCursor(Qt.PointingHandCursor)
        self.btn_settings.setFont(QFont(self.font_family, 9, QFont.Bold))
        self.btn_settings.setStyleSheet(
            "QPushButton {"
            "  background-color: rgba(255,255,255,0);"
            "  color: rgb(35, 35, 35);"
            "  border: 0px solid rgba(255,255,255,70);"
            "  border-radius: 10px;"
            "}"
            "QPushButton:hover {"
            "  background-color: rgba(255,255,255,140);"
            "}"
            "QPushButton:pressed {"
            "  background-color: rgba(255,255,255,120);"
            "}"
        )
        self.btn_settings.clicked.connect(self._open_settings)
        self.btn_settings.raise_()

        # TITULO
        self.title_label = QLabel("KONNOR", self.central_widget)
        self.title_label.setGeometry(0, 28, W, 52)
        self.title_label.setAlignment(Qt.AlignCenter)
        self.title_label.setFont(QFont(self.font_family, 38, QFont.Bold))
        self.title_label.setStyleSheet("color: #ffffff; background: transparent;")
        self.title_label.setAttribute(Qt.WA_TransparentForMouseEvents)

        self.subtitle_label = QLabel("LAUNCHER", self.central_widget)
        self.subtitle_label.setGeometry(0, 78, W, 24)
        self.subtitle_label.setAlignment(Qt.AlignCenter)
        self.subtitle_label.setFont(QFont(self.font_family, 13))
        self.subtitle_label.setStyleSheet("color: rgba(255,255,255,150); background: transparent;")
        self.subtitle_label.setAttribute(Qt.WA_TransparentForMouseEvents)

        # MENSAJE DE BIENVENIDA
        props = launcher_core.load_properties()
        username = props.get("player.username", "")
        self.welcome_label = QLabel(f"¡Bienvenido {username}!" if username else "", self.central_widget)
        self.welcome_label.setGeometry(0, H - 128, W, 22)
        self.welcome_label.setAlignment(Qt.AlignCenter)
        welcome_font = QFont(self.font_family, 12)
        welcome_font.setItalic(True)
        self.welcome_label.setFont(welcome_font)
        self.welcome_label.setStyleSheet("color: rgba(255, 255, 255, 190); background: transparent;")
        self.welcome_label.setAttribute(Qt.WA_TransparentForMouseEvents)
        if not username:
            self.welcome_label.hide()

        # INFO jugador, RAM, version (inferior izquierda)
        props = launcher_core.load_properties()
        
        # Mostrar versión cacheada al instante (sin bloquear)
        cached = props.get("cached.client.version", "")
        version_text = f"v{cached}" if cached else "v-"
        self.version_label = QLabel(version_text, self.central_widget)
        self.version_label.setGeometry(36, H - 30, 180, 18)
        self.version_label.setFont(QFont(self.font_family, 9))
        self.version_label.setStyleSheet("color: rgba(255,255,255,220); background: transparent;")

        # BOTON VERSION (inferior derecha, menu abre hacia arriba)
        self.version_button = QPushButton("VERSION", self.central_widget)
        self.version_button.setGeometry(W - 152, H - 56, 128, 34)
        self.version_button.setFont(QFont(self.font_family, 9, QFont.Bold))
        self.version_button.setCursor(Qt.PointingHandCursor)
        self.version_button.setStyleSheet(
            "QPushButton {"
            "  background-color: rgba(30, 35, 42, 230);"
            "  color: rgba(255, 255, 255, 210);"
            "  border: 1px solid rgba(255, 255, 255, 40);"
            "  border-radius: 17px;"
            "}"
            "QPushButton:hover {"
            "  background-color: rgba(40, 45, 55, 240);"
            "  border-color: rgba(255, 255, 255, 80);"
            "}"
            "QPushButton:disabled { background-color: rgba(30, 35, 42, 100); color: rgba(255, 255, 255, 60); border: none; }"
        )

        # VENTANA POPUP DE VERSIONES (Scrollable)
        from PySide6.QtWidgets import QListWidget, QVBoxLayout
        self.version_popup = QWidget(self.window)
        self.version_popup.setWindowFlags(Qt.Popup | Qt.FramelessWindowHint | Qt.NoDropShadowWindowHint)
        self.version_popup.setAttribute(Qt.WA_TranslucentBackground)
        
        self.version_list = QListWidget(self.version_popup)
        self.version_list.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.version_list.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.version_list.setStyleSheet(
            "QListWidget {"
            "  background-color: rgba(25, 30, 38, 245);"
            "  color: rgba(255, 255, 255, 200);"
            "  font-weight: bold;"
            "  border: 1px solid rgba(255, 255, 255, 40);"
            "  border-radius: 8px;"
            "  padding: 4px;"
            "  outline: none;"
            "}"
            "QListWidget::item {"
            "  padding: 8px 12px;"
            "  margin: 2px;"
            "  border-radius: 4px;"
            "}"
            "QListWidget::item:hover {"
            "  background-color: rgba(255, 255, 255, 15);"
            "}"
            "QListWidget::item:selected {"
            "  background-color: rgba(255, 255, 255, 30);"
            "  color: #ffffff;"
            "}"
            "QScrollBar:vertical {"
            "  border: none;"
            "  background: transparent;"
            "  width: 6px;"
            "  margin: 4px 0px 4px 0px;"
            "}"
            "QScrollBar::handle:vertical {"
            "  background: rgba(255, 255, 255, 60);"
            "  border-radius: 3px;"
            "}"
            "QScrollBar::handle:vertical:hover {"
            "  background: rgba(255, 255, 255, 100);"
            "}"
            "QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {"
            "  border: none; background: none;"
            "}"
        )
        self.version_list.itemClicked.connect(self._on_version_item_clicked)
        
        popup_layout = QVBoxLayout(self.version_popup)
        popup_layout.setContentsMargins(0, 0, 0, 0)
        popup_layout.addWidget(self.version_list)
        
        self.version_button.clicked.connect(self._show_version_menu_above)

        # ESTADO Y PROGRESO (arriba del boton jugar)
        bar_w = 420
        bar_x = (W - bar_w) // 2

        self.action_label = QLabel("", self.central_widget)
        self.action_label.setGeometry(bar_x, H - 130, bar_w, 18)
        self.action_label.setAlignment(Qt.AlignCenter)
        self.action_label.setFont(QFont(self.font_family, 10, QFont.Bold))
        self.action_label.setStyleSheet("color: #ffffff; background: transparent;")

        self.progress_bar = QProgressBar(self.central_widget)
        self.progress_bar.setGeometry(bar_x, H - 106, bar_w, 6)
        self.progress_bar.setTextVisible(False)
        self.progress_bar.setStyleSheet(
            "QProgressBar {"
            "  border: none;"
            "  border-radius: 3px;"
            "  background: rgba(255,255,255,18);"
            "}"
            "QProgressBar::chunk {"
            "  background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #3ab55e, stop:1 #72e89a);"
            "  border-radius: 3px;"
            "}"
        )
        self.progress_bar.hide()

        # BOTON JUGAR
        btn_w = 260
        btn_h = 52
        self.play_button = QPushButton("JUGAR", self.central_widget)
        self.play_button.setGeometry((W - btn_w) // 2, H - 92, btn_w, btn_h)
        self.play_button.setFont(QFont(self.font_family, 16, QFont.Bold))
        self.play_button.setCursor(Qt.PointingHandCursor)
        self.play_button.clicked.connect(self.start_verify)



    def _show_version_menu_above(self):
        if self.version_popup.isVisible():
            self.version_popup.hide()
            return
            
        self._refresh_version_menu()
        btn = self.version_button
        
        # Calcular altura para ~6 items
        item_height = 32  # aproximado basado en el padding
        max_items = 6
        count = self.version_list.count()
        visible_items = min(count, max_items)
        # 8px de padding vertical del popup + altura de los items
        popup_height = (visible_items * item_height) + 12
        if count == 0:
            popup_height = 40
            
        popup_width = max(btn.width() + 40, 180)
        self.version_popup.setFixedSize(popup_width, popup_height)
        
        global_pos = btn.mapToGlobal(QPoint(0, 0))
        
        # Alinear el borde derecho del menú con el borde derecho del botón
        popup_x = global_pos.x() + btn.width() - popup_width
        # Colocarlo exactamente arriba con 4px de separación
        popup_y = global_pos.y() - popup_height - 4
        
        self.version_popup.move(popup_x, popup_y)
        self.version_popup.show()
        self.version_list.setFocus()

    def _on_version_item_clicked(self, item):
        version = item.data(Qt.UserRole)
        if version:
            self.select_version(version)
        self.version_popup.hide()

    def _open_settings(self):
        dlg = SettingsDialog(parent=self.window, font_family=self.font_family)
        if dlg.exec() == QDialog.Accepted:
            # Recargar etiquetas con los nuevos valores
            try:
                props = launcher_core.load_properties()
            except Exception:
                pass

    def _refresh_version_menu(self):
        self.version_list.clear()
        versions = launcher_core.get_all_versions()
        installed = launcher_core.get_installed_versions()
        
        self.version_button.setEnabled(True)
        from PySide6.QtWidgets import QListWidgetItem
        
        # Si aún no carga el caché, avisar al usuario
        if launcher_core._mojang_versions_cache is None:
            loading_item = QListWidgetItem("Cargando versiones oficiales...")
            loading_item.setFlags(Qt.NoItemFlags)
            self.version_list.addItem(loading_item)

        if not versions and launcher_core._mojang_versions_cache is not None:
            item = QListWidgetItem("No hay versiones disponibles")
            item.setFlags(Qt.NoItemFlags)
            self.version_list.addItem(item)
            return

        for version in versions:
            display_text = version if version in installed else f"{version}  (Descargar ↓)"
            item = QListWidgetItem(display_text)
            item.setData(Qt.UserRole, version)
            self.version_list.addItem(item)

    def select_version(self, version):
        try:
            launcher_core.set_selected_version(version)
            self.update_version_label(version)
        except Exception as ex:
            QMessageBox.warning(self.window, "Error", f"No se pudo guardar la version:\n{ex}")

    def update_version_label(self, version):
        pass  # El label muestra la version del launcher, no la del juego

    def _apply_styles(self):
        self.central_widget.setStyleSheet("background: transparent;")
        self.window.setStyleSheet("QMainWindow { background: transparent; }")
        self.play_button.setStyleSheet(
            "QPushButton {"
            "  background: qlineargradient(x1:0, y1:0, x2:0, y2:1,"
            "    stop:0.00 #7ae08a,"
            "    stop:0.08 #7ae08a,"
            "    stop:0.09 #4fbf60,"
            "    stop:0.84 #4fbf60,"
            "    stop:0.85 #2d7a3c,"
            "    stop:1.00 #2d7a3c);"
            "  color: #ffffff;"
            "  border-radius: 10px;"
            "  border: 0px solid rgba(0, 0, 0, 255);"
            "}"
            
            "QPushButton:hover {"
            "  background: qlineargradient(x1:0, y1:0, x2:0, y2:1,"
            "    stop:0.00 #94efaa,"
            "    stop:0.12 #94efaa,"
            "    stop:0.13 #62d478,"
            "    stop:0.84 #62d478,"
            "    stop:0.85 #3a9a50,"
            "    stop:1.00 #3a9a50);"
            "  border: 0px solid rgba(0, 0, 0, 255);"
            "}"
            
            "QPushButton:pressed {"
            "  background: qlineargradient(x1:0, y1:0, x2:0, y2:1,"
            "    stop:0.00 #2d7a3c,"
            "    stop:0.15 #2d7a3c,"
            "    stop:0.16 #3da050,"
            "    stop:0.91 #3da050,"
            "    stop:0.92 #5ecc70,"
            "    stop:1.00 #5ecc70);"
            "  border: 0px solid rgba(0, 0, 0, 255);"
            "}"
            
            "QPushButton:disabled {"
            "  background: qlineargradient(x1:0, y1:0, x2:0, y2:1,"
            "    stop:0.00 #2d7a3c,"
            "    stop:0.15 #2d7a3c,"
            "    stop:0.16 #3da050,"
            "    stop:0.91 #3da050,"
            "    stop:0.92 #5ecc70,"
            "    stop:1.00 #5ecc70);"
            "  border: 0px solid rgba(0, 0, 0, 255);"
            "}"
            
            #"QPushButton:disabled {"
            #"  background: rgba(60, 80, 60, 180);"
            #"  color: rgba(255,255,255,60);"
            #"  border: 3px solid rgba(0,0,0,50);"
            #"}"
        )

    def _run_verification(self):
        self.verify_runnable = VerifyRunnable()
        self.verify_runnable.signals.status_updated.connect(self.update_action)
        self.verify_runnable.signals.finished.connect(self._verification_finished)
        QThreadPool.globalInstance().start(self.verify_runnable)

    def start_verify(self):
        if hasattr(self, 'welcome_label'):
            self.welcome_label.hide()
        self.play_button.setText("VERIFICANDO")
        self.play_button.setEnabled(False)
        self.update_action("")
        self._run_verification()

    def _verification_finished(self, result):
        if result == 0:
            self.update_action("")
            self._run_launch()
        else:
            if not self.action_label.text().lower().startswith("error"):
                self.update_action("Error de conexión / Verificación fallida")
            self.play_button.setEnabled(True)
            self.play_button.setText("JUGAR")

    def _run_launch(self):
        self.update_action("")
        self.play_button.setText("INICIANDO JUEGO")
        self.launch_runnable = LaunchRunnable()
        self.launch_runnable.signals.finished.connect(self._launch_finished)
        self.launch_runnable.signals.status_updated.connect(self.update_action)
        QThreadPool.globalInstance().start(self.launch_runnable)

    def _launch_finished(self, result):
        if result == 0:
            self.update_action("")
        else:
            if not self.action_label.text().lower().startswith("error"):
                self.update_action("Error iniciando el juego")
        self.play_button.setEnabled(True)
        self.play_button.setText("JUGAR")

    def update_status(self, text):
        pass

    def update_action(self, text):
        normalized = text.strip()
        lower = normalized.lower()

        if lower.startswith("mc_progress|"):
            try:
                # Formato: MC_PROGRESS|texto|current|total
                parts = text.split("|")
                if len(parts) >= 4:
                    self.action_label.setText(parts[1])
                    self.progress_bar.show()
                    current = int(parts[2])
                    total = int(parts[3])
                    if total > 0:
                        self.progress_bar.setValue(int((current / total) * 100))
            except Exception:
                pass
            return

        if lower.startswith(("descargando bloque", "descargando archivos", "extrayendo")):
            self.action_label.setText(normalized)
            self.progress_bar.show()
            try:
                parts = lower.split(":")[-1].strip().split("/")
                if len(parts) == 2:
                    current = float(parts[0].replace("mb", "").strip())
                    total = float(parts[1].replace("mb", "").strip())
                    if total > 0:
                        self.progress_bar.setValue(int((current / total) * 100))
            except Exception:
                pass
            return

        self.progress_bar.hide()
        self.action_label.setText(normalized)

    def run(self):
        self.window.show()
        # Consultar versión del servidor en segundo plano (no bloquea la UI)
        self._fetch_version_async()
        return self.app.exec()

    def _fetch_version_async(self):
        runnable = VersionFetchRunnable()
        runnable.signals.version_ready.connect(self._on_version_fetched)
        QThreadPool.globalInstance().start(runnable)

    def _on_version_fetched(self, version):
        self.version_label.setText(f"v{version}")
