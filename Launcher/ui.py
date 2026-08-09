import os
import sys
import launcher_core
from PySide6.QtCore import QObject, QRunnable, QThreadPool, Signal, Qt
from PySide6.QtGui import QFontDatabase, QFont, QPixmap
from PySide6.QtWidgets import QApplication, QLabel, QMainWindow, QMessageBox, QPushButton, QMenu, QWidget


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
            result = launcher_core.verify_client(self.signals.status_updated.emit)
        except Exception as ex:
            self.signals.status_updated.emit(f"Error de verificación: {ex}")
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


class LauncherUI:
    WINDOW_WIDTH = 760
    WINDOW_HEIGHT = 520

    def __init__(self):
        self.app = QApplication.instance() or QApplication(sys.argv)
        self.window = QMainWindow()
        self.window.setWindowTitle("Fkonnor Launcher")
        self.window.setFixedSize(self.WINDOW_WIDTH, self.WINDOW_HEIGHT)

        self.central_widget = QWidget()
        self.window.setCentralWidget(self.central_widget)

        self._load_fonts()
        self._build_ui()
        self._apply_styles()

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
        self.background_label = QLabel(self.central_widget)
        self.background_label.setGeometry(0, 0, self.WINDOW_WIDTH, self.WINDOW_HEIGHT)
        self.background_label.setScaledContents(True)

        image_path = os.path.join(os.path.dirname(__file__), "background.png")
        if os.path.isfile(image_path):
            pixmap = QPixmap(image_path)
            if not pixmap.isNull():
                self.background_label.setPixmap(
                    pixmap.scaled(
                        self.WINDOW_WIDTH,
                        self.WINDOW_HEIGHT,
                        Qt.KeepAspectRatioByExpanding,
                        Qt.SmoothTransformation,
                    )
                )
            else:
                self.background_label.setStyleSheet(
                    "background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d1c2e, stop:1 #192f4a);"
                )
        else:
            self.background_label.setStyleSheet(
                "background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d1c2e, stop:1 #192f4a);"
            )

        config = launcher_core.load_config()
        username = config["player"]["username"]
        memory = config["java"]["memory"]

        self.title_label = QLabel("FKONNOR LAUNCHER", self.central_widget)
        self.title_label.setGeometry(0, 30, self.WINDOW_WIDTH, 40)
        self.title_label.setAlignment(Qt.AlignCenter)
        self.title_label.setFont(QFont(self.font_family, 24, QFont.Bold))
        self.title_label.setStyleSheet("color: #f2f7ff; background: transparent;")

        self.user_label = QLabel(f"Jugador: {username}", self.central_widget)
        self.user_label.setGeometry(36, 120, 300, 22)
        self.user_label.setFont(QFont(self.font_family, 11))
        self.user_label.setStyleSheet("color: #d7e8ff; background: transparent;")

        self.ram_label = QLabel(f"RAM: {memory}", self.central_widget)
        self.ram_label.setGeometry(36, 145, 300, 22)
        self.ram_label.setFont(QFont(self.font_family, 11))
        self.ram_label.setStyleSheet("color: #d7e8ff; background: transparent;")

        # QLabel de estado aislado para uso futuro
        # self.status_label = QLabel("Estado: listo", self.central_widget)
        # self.status_label.setGeometry(36, 180, 320, 24)
        # self.status_label.setFont(QFont(self.font_family, 13, QFont.Bold))
        # self.status_label.setStyleSheet("color: #b8f6b2; background: transparent;")
        # self.status_label.hide()

        self.action_label = QLabel("", self.central_widget)
        self.action_label.setGeometry(0, self.WINDOW_HEIGHT - 158, self.WINDOW_WIDTH, 24)
        self.action_label.setAlignment(Qt.AlignCenter)
        self.action_label.setFont(QFont(self.font_family, 12))
        self.action_label.setStyleSheet("color: #ffffff; background: transparent;")

        selected_version = launcher_core.get_selected_version()
        version_text = f"Versión: {selected_version}" if selected_version else "Versión: no definida"
        self.version_label = QLabel(version_text, self.central_widget)
        self.version_label.setGeometry(36, self.WINDOW_HEIGHT - 42, 220, 18)
        self.version_label.setFont(QFont(self.font_family, 10))
        self.version_label.setStyleSheet("color: #a9b6be; background: transparent;")

        self.version_button = QPushButton("VERSION", self.central_widget)
        self.version_button.setGeometry(self.WINDOW_WIDTH - 156, self.WINDOW_HEIGHT - 52, 120, 38)
        self.version_button.setFont(QFont(self.font_family, 10, QFont.Bold))
        self.version_button.setCursor(Qt.PointingHandCursor)
        self.version_button.setStyleSheet(
            "QPushButton {"
            "background-color: rgba(255, 255, 255, 40);"
            "color: #f8fbff;"
            "border: 1px solid rgba(255, 255, 255, 80);"
            "border-radius: 19px;"
            "text-transform: uppercase;"
            "}"
            "QPushButton:hover { background-color: rgba(255, 255, 255, 55); }"
        )

        self.version_menu = QMenu(self.window)
        self.version_menu.setStyleSheet(
            "QMenu { background-color: rgba(18, 26, 38, 230); color: #f2f7ff; border: 1px solid rgba(255,255,255,18); }"
            "QMenu::item:selected { background-color: rgba(76,192,107,180); }"
        )
        self.version_button.setMenu(self.version_menu)
        self.version_menu.aboutToShow.connect(self._refresh_version_menu)

        self.play_button = QPushButton("JUGAR", self.central_widget)
        self.play_button.setGeometry((self.WINDOW_WIDTH - 280) // 2, self.WINDOW_HEIGHT - 110, 280, 70)
        self.play_button.setFont(QFont(self.font_family, 16, QFont.Bold))
        self.play_button.clicked.connect(self.start_verify)

    def _refresh_version_menu(self):
        self.version_menu.clear()
        versions = launcher_core.get_installed_versions()

        if not versions:
            self.version_button.setEnabled(False)
            action = self.version_menu.addAction("No hay versiones instaladas")
            action.setEnabled(False)
            return

        self.version_button.setEnabled(True)
        for version in versions:
            action = self.version_menu.addAction(version)
            action.triggered.connect(lambda checked, version=version: self.select_version(version))

    def select_version(self, version):
        try:
            launcher_core.set_selected_version(version)
            self.update_version_label(version)
            self.update_status(f"Versión seleccionada: {version}")
        except Exception as ex:
            QMessageBox.warning(self.window, "Error", f"No se pudo guardar la versión:\n{ex}")

    def update_version_label(self, version):
        self.version_label.setText(f"Versión: {version}")

    def _apply_styles(self):
        self.central_widget.setStyleSheet("background: transparent;")
        self.window.setStyleSheet("QMainWindow { background: transparent; }")
        self.play_button.setStyleSheet(
            "QPushButton {"
            "background-color: #4cc06b;"
            "color: white;"
            "border-radius: 34px;"
            "border: none;"
            "}"
            "QPushButton:hover { background-color: #61d67f; }"
            "QPushButton:disabled { background-color: #2f7a48; color: #d4e9ce; }"
        )

    def _run_verification(self):
        self.verify_runnable = VerifyRunnable()
        self.verify_runnable.signals.status_updated.connect(self.update_action)
        self.verify_runnable.signals.finished.connect(self._verification_finished)
        QThreadPool.globalInstance().start(self.verify_runnable)

    def start_verify(self):
        self.play_button.setText("EN EJECUCIÓN")
        self.play_button.setEnabled(False)
        self.update_action("verificando archivos")
        self._run_verification()

    def _verification_finished(self, result):
        if result == 0:
            if self.action_label.text().lower() != "verificador desactivado":
                self.update_action("verificado")
            self._run_launch()
        else:
            # Si ya se detectó servidor no responde, no sobreescribir ese mensaje.
            if self.action_label.text().lower() != "servidor no responde":
                self.update_action("Error verificando")
            self.play_button.setEnabled(True)
            self.play_button.setText("JUGAR")

    def _run_launch(self):
        self.launch_runnable = LaunchRunnable()
        self.launch_runnable.signals.finished.connect(self._launch_finished)
        self.launch_runnable.signals.status_updated.connect(self.update_action)
        QThreadPool.globalInstance().start(self.launch_runnable)

    def _launch_finished(self, result):
        if result == 0:
            self.update_action("Juego cerrado")
        else:
            self.update_action("Error iniciando el juego")
        self.play_button.setEnabled(True)
        self.play_button.setText("JUGAR")

    def update_status(self, text):
        # status_label desactivado para uso futuro
        pass

    def update_action(self, text):
        normalized = text.strip()
        lower = normalized.lower()
        if lower.startswith("descargando archivos"):
            self.action_label.setText(normalized)
            return
        if lower in [
            "verificando archivos",
            "verificado",
            "servidor no responde",
            "verificador desactivado",
            "juego cerrado",
            "error verificando",
            "error al iniciar el juego"
        ]:
            self.action_label.setText(normalized)
            return
        # Ignorar otros mensajes y conservar el estado actual.

    def run(self):
        self.window.show()
        return self.app.exec()
