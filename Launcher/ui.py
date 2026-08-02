import os
import sys
import launcher_core
from PySide6.QtCore import QObject, QThread, Signal, Qt
from PySide6.QtGui import QFontDatabase, QFont, QPixmap
from PySide6.QtWidgets import QApplication, QLabel, QMainWindow, QPushButton, QWidget


class VerifyWorker(QObject):
    status_updated = Signal(str)
    finished = Signal(int)

    def run(self):
        result = launcher_core.verify_client(self.status_updated.emit)
        self.finished.emit(result)


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

        self.status_label = QLabel("Estado: listo", self.central_widget)
        self.status_label.setGeometry(36, 180, 320, 24)
        self.status_label.setFont(QFont(self.font_family, 13, QFont.Bold))
        self.status_label.setStyleSheet("color: #b8f6b2; background: transparent;")

        self.version_label = QLabel("Version 1.0.0", self.central_widget)
        self.version_label.setGeometry(36, self.WINDOW_HEIGHT - 42, 160, 18)
        self.version_label.setFont(QFont(self.font_family, 10))
        self.version_label.setStyleSheet("color: #a9b6be; background: transparent;")

        self.play_button = QPushButton("JUGAR", self.central_widget)
        self.play_button.setGeometry((self.WINDOW_WIDTH - 280) // 2, self.WINDOW_HEIGHT - 110, 280, 70)
        self.play_button.setFont(QFont(self.font_family, 16, QFont.Bold))
        self.play_button.clicked.connect(self.start_verify)

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
        self.worker = VerifyWorker()
        self.worker_thread = QThread()
        self.worker.moveToThread(self.worker_thread)
        self.worker_thread.started.connect(self.worker.run)
        self.worker.status_updated.connect(self.update_status)
        self.worker.finished.connect(self._verification_finished)
        self.worker.finished.connect(self.worker_thread.quit)
        self.worker.finished.connect(self.worker.deleteLater)
        self.worker_thread.finished.connect(self.worker_thread.deleteLater)
        self.worker_thread.start()

    def start_verify(self):
        self.play_button.setText("EN EJECUCIÓN")
        self.play_button.setEnabled(False)
        self.update_status("Verificando...")
        self._run_verification()

    def _verification_finished(self, result):
        if result == 0:
            self.update_status("Cliente listo")
            launcher_core.launch_game()
        else:
            self.update_status("Error verificando")
            self.play_button.setEnabled(True)
            self.play_button.setText("JUGAR")

    def update_status(self, text):
        self.status_label.setText(text)

    def run(self):
        self.window.show()
        return self.app.exec()
