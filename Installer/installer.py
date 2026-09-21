import os
import sys
import shutil
import subprocess
from pathlib import Path

from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, 
                               QLabel, QPushButton, QProgressBar, QMessageBox)
from PySide6.QtCore import Qt, QThread, Signal
from PySide6.QtGui import QFont, QIcon, QPixmap

def get_resource_path(relative_path):
    if hasattr(sys, '_MEIPASS'):
        return os.path.join(sys._MEIPASS, relative_path)
    return os.path.join(os.path.abspath(os.path.dirname(__file__)), relative_path)

class InstallWorker(QThread):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(bool, str)

    def run(self):
        try:
            self.status.emit("Preparando instalación...")
            self.progress.emit(10)
            
            # Directorio destino: %APPDATA%\KonnorLauncher
            appdata = os.getenv("APPDATA")
            if not appdata:
                appdata = os.path.join(os.path.expanduser("~"), "AppData", "Roaming")
                
            install_dir = os.path.join(appdata, "KonnorLauncher")
            os.makedirs(install_dir, exist_ok=True)
            
            self.status.emit("Copiando archivos del launcher...")
            self.progress.emit(30)
            
            # Archivos a copiar
            exe_src = get_resource_path("Fkonnor Launcher.exe")
            ico_src = get_resource_path("icon.ico")
            prop_src = get_resource_path("launcher.properties")
            
            if not os.path.isfile(exe_src):
                self.finished.emit(False, "No se encontró el ejecutable principal para instalar.")
                return
                
            exe_dest = os.path.join(install_dir, "Fkonnor Launcher.exe")
            ico_dest = os.path.join(install_dir, "icon.ico")
            prop_dest = os.path.join(install_dir, "launcher.properties")
            
            shutil.copy2(exe_src, exe_dest)
            self.progress.emit(60)
            
            if os.path.isfile(ico_src):
                shutil.copy2(ico_src, ico_dest)
                
            if os.path.isfile(prop_src) and not os.path.isfile(prop_dest):
                shutil.copy2(prop_src, prop_dest)
            
            self.status.emit("Creando acceso directo en el escritorio...")
            self.progress.emit(80)
            
            # Crear acceso directo usando VBScript
            desktop = os.path.join(os.path.expanduser("~"), "Desktop")
            shortcut_path = os.path.join(desktop, "Fkonnor Launcher.lnk")
            
            vbs_path = os.path.join(install_dir, "create_shortcut.vbs")
            vbs_content = f"""
Set oWS = WScript.CreateObject("WScript.Shell")
sLinkFile = "{shortcut_path}"
Set oLink = oWS.CreateShortcut(sLinkFile)
oLink.TargetPath = "{exe_dest}"
oLink.WorkingDirectory = "{install_dir}"
"""
            if os.path.isfile(ico_dest):
                vbs_content += f'oLink.IconLocation = "{ico_dest}"\n'
                
            vbs_content += 'oLink.Save()\n'
            
            with open(vbs_path, "w", encoding="utf-8") as f:
                f.write(vbs_content)
                
            subprocess.run(["cscript", "//nologo", vbs_path], creationflags=subprocess.CREATE_NO_WINDOW)
            os.remove(vbs_path)
            
            self.progress.emit(100)
            self.status.emit("¡Instalación completada!")
            self.finished.emit(True, "Fkonnor Launcher se instaló correctamente.")
            
        except Exception as e:
            self.finished.emit(False, str(e))

class InstallerWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Instalador - Fkonnor Launcher")
        self.setFixedSize(450, 250)
        
        ico_path = get_resource_path("icon.ico")
        if os.path.isfile(ico_path):
            self.setWindowIcon(QIcon(ico_path))
            
        self.setStyleSheet("""
            QMainWindow {
                background-color: #1E1E1E;
            }
            QLabel {
                color: white;
                font-family: Arial;
            }
            QPushButton {
                background-color: #2ECC71;
                color: white;
                border: none;
                border-radius: 4px;
                padding: 8px 16px;
                font-weight: bold;
                font-size: 14px;
            }
            QPushButton:hover {
                background-color: #27AE60;
            }
            QPushButton:disabled {
                background-color: #555555;
                color: #888888;
            }
            QProgressBar {
                border: none;
                background-color: #333333;
                border-radius: 4px;
                text-align: center;
                color: transparent;
            }
            QProgressBar::chunk {
                background-color: #2ECC71;
                border-radius: 4px;
            }
        """)

        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        
        layout = QVBoxLayout(central_widget)
        layout.setContentsMargins(30, 30, 30, 30)
        layout.setSpacing(15)
        
        self.title_label = QLabel("Instalación de Fkonnor Launcher")
        self.title_label.setFont(QFont("Arial", 16, QFont.Bold))
        self.title_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.title_label)
        
        self.info_label = QLabel("Se instalará el launcher en AppData y se creará un acceso directo.")
        self.info_label.setWordWrap(True)
        self.info_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.info_label)
        
        self.status_label = QLabel("")
        self.status_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.status_label)
        
        self.progress_bar = QProgressBar()
        self.progress_bar.setFixedHeight(8)
        self.progress_bar.setValue(0)
        self.progress_bar.hide()
        layout.addWidget(self.progress_bar)
        
        layout.addStretch()
        
        self.install_button = QPushButton("Instalar")
        self.install_button.setCursor(Qt.PointingHandCursor)
        self.install_button.clicked.connect(self.start_installation)
        layout.addWidget(self.install_button)
        
        self.worker = None

    def start_installation(self):
        self.install_button.setEnabled(False)
        self.progress_bar.show()
        
        self.worker = InstallWorker()
        self.worker.progress.connect(self.progress_bar.setValue)
        self.worker.status.connect(self.status_label.setText)
        self.worker.finished.connect(self.on_finished)
        self.worker.start()

    def on_finished(self, success, message):
        if success:
            self.install_button.setEnabled(True)
            self.install_button.setText("Cerrar")
            self.install_button.clicked.disconnect(self.start_installation)
            self.install_button.clicked.connect(self.close)
        else:
            QMessageBox.critical(self, "Error de Instalación", f"Ocurrió un error:\n{message}")
            self.install_button.setEnabled(True)
            self.install_button.setText("Reintentar")

if __name__ == "__main__":
    app = QApplication(sys.argv)
    window = InstallerWindow()
    window.show()
    sys.exit(app.exec())

