# Punto de entrada del Gestor de Servidores Fkonnor
import sys
import os

# Agregar la carpeta actual al path para imports relativos
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from PySide6.QtWidgets import QApplication
from server_ui import ServerManagerWindow

def main():
    app = QApplication(sys.argv)
    window = ServerManagerWindow()
    window.show()
    sys.exit(app.exec())

if __name__ == "__main__":
    main()

