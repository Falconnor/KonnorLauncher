import os
import sys

# Habilitar escala de alta resolución ANTES de crear QApplication
os.environ.setdefault("QT_ENABLE_HIGHDPI_SCALING", "1")
os.environ.setdefault("QT_SCALE_FACTOR_ROUNDING_POLICY", "PassThrough")

from PySide6.QtWidgets import QApplication
from PySide6.QtCore import Qt
from PySide6.QtGui import QFont

# Crear QApplication antes que cualquier widget
if not QApplication.instance():
    _app = QApplication(sys.argv)

    # Fuente base con antialiasing forzado
    default_font = _app.font()
    default_font.setStyleStrategy(QFont.PreferAntialias | QFont.PreferOutline)
    default_font.setHintingPreference(QFont.PreferNoHinting)
    _app.setFont(default_font)

from ui import LauncherUI

if __name__ == "__main__":
    app = LauncherUI()
    app.run()