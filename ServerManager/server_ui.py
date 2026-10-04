# -*- coding: utf-8 -*-
import sys
import os
import json
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
                               QLabel, QLineEdit, QComboBox, QSlider, QProgressBar, QPushButton,
                               QScrollArea, QFrame, QDialog, QSpinBox, QCheckBox, QTextEdit, 
                               QStackedWidget, QMessageBox, QSpacerItem, QSizePolicy, QFormLayout)
from PySide6.QtCore import Qt, QThread, Signal, Slot, QTimer, QProcess
from PySide6.QtGui import QFont, QShortcut, QKeySequence, QColor, QPalette

try:
    import server_core
except ImportError:
    # Dummy mock for structural completeness if server_core is not present
    class DummyCore:
        def get_servers(self): return []
        def fetch_versions(self, type): return ["1.20.4", "1.20.1"]
        def create_server(self, name, type, version, ram, cb): pass
        def get_server_config(self, name): return {}
        def save_server_config(self, name, cfg): pass
        def start_server(self, name, output_cb): pass
        def stop_server(self, name): pass
        def send_command(self, name, cmd): pass
        def delete_server(self, name): pass
    server_core = DummyCore()

# ==========================================
# HOJAS DE ESTILO GLOBAL
# ==========================================
STYLE_SHEET = """
QMainWindow { background-color: #1E1E1E; }
QWidget { background-color: #1E1E1E; color: white; }
QLabel { color: white; font-family: Arial; background: transparent; }
QComboBox QAbstractItemView { background-color: #2A2A2A; color: white; selection-background-color: #2ECC71; selection-color: white; }
QLineEdit, QComboBox, QSpinBox { background-color: #2A2A2A; color: white; border: 1px solid #444; border-radius: 4px; padding: 6px; font-size: 13px; }
QPushButton { background-color: #2ECC71; color: white; border: none; border-radius: 4px; padding: 8px 16px; font-weight: bold; font-size: 13px; }
QPushButton:hover { background-color: #27AE60; }
QPushButton:disabled { background-color: #555; color: #888; }
QCheckBox { color: white; background: transparent; }
QCheckBox::indicator { width: 18px; height: 18px; }
QSlider::groove:horizontal { background: #333; height: 6px; border-radius: 3px; }
QSlider::handle:horizontal { background: #2ECC71; width: 16px; height: 16px; border-radius: 8px; margin: -5px 0; }
QScrollArea { border: none; background-color: transparent; }
QScrollArea > QWidget > QWidget { background-color: transparent; }
QProgressBar { border: none; background-color: #333; border-radius: 4px; text-align: center; color: transparent; }
QProgressBar::chunk { background-color: #2ECC71; border-radius: 4px; }
QFrame#ServerCard { background-color: #2A2A2A; border-radius: 8px; padding: 10px; }
"""

# ==========================================
# WORKERS DE QTHREAD PARA OPERACIONES BLOQUEANTES
# ==========================================

class FetchVersionsWorker(QThread):
    """Hilo para obtener versiones de Minecraft según el tipo."""
    finished = Signal(list)

    def __init__(self, server_type):
        super().__init__()
        self.server_type = server_type

    def run(self):
        try:
            versions = server_core.fetch_versions(self.server_type)
            self.finished.emit(versions)
        except Exception as e:
            self.finished.emit([])

class CreateServerWorker(QThread):
    """Hilo para crear un servidor con seguimiento de progreso."""
    progress = Signal(int, str)
    finished = Signal(bool, str)

    def __init__(self, name, s_type, version, ram):
        super().__init__()
        self.name = name
        self.s_type = s_type
        self.version = version
        self.ram = ram

    def run(self):
        try:
            def callback(p, text):
                self.progress.emit(p, text)
            
            server_core.create_server(self.name, self.s_type, self.version, self.ram, callback)
            self.finished.emit(True, "Servidor creado con éxito")
        except Exception as e:
            self.finished.emit(False, str(e))

class RunServerWorker(QThread):
    """Hilo para ejecutar el servidor y leer la salida de la consola."""
    output = Signal(str)
    stopped = Signal()

    def __init__(self, name):
        super().__init__()
        self.name = name

    def run(self):
        def cb(line):
            self.output.emit(line)
        server_core.start_server(self.name, cb)
        self.stopped.emit()

# ==========================================
# DIALOGO DE CREACION DE SERVIDOR
# ==========================================

class CreateServerDialog(QDialog):
    """Diálogo modal para crear un nuevo servidor."""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Crear Nuevo Servidor")
        self.setFixedSize(400, 350)
        self.setStyleSheet(STYLE_SHEET)
        
        layout = QVBoxLayout(self)
        
        # Nombre
        self.name_input = QLineEdit()
        self.name_input.setPlaceholderText("Nombre del servidor")
        layout.addWidget(QLabel("Nombre:"))
        layout.addWidget(self.name_input)
        
        # Tipo
        self.type_combo = QComboBox()
        self.type_combo.addItems(["Vanilla", "Paper", "Fabric", "Forge"])
        self.type_combo.currentTextChanged.connect(self.on_type_changed)
        layout.addWidget(QLabel("Tipo:"))
        layout.addWidget(self.type_combo)
        
        # Versión
        self.version_combo = QComboBox()
        layout.addWidget(QLabel("Versión:"))
        layout.addWidget(self.version_combo)
        
        # RAM
        self.ram_layout = QHBoxLayout()
        self.ram_slider = QSlider(Qt.Horizontal)
        self.ram_slider.setRange(1, 16)
        self.ram_slider.setValue(4)
        self.ram_label = QLabel("4 GB")
        self.ram_slider.valueChanged.connect(lambda v: self.ram_label.setText(f"{v} GB"))
        
        self.ram_layout.addWidget(self.ram_slider)
        self.ram_layout.addWidget(self.ram_label)
        layout.addWidget(QLabel("RAM:"))
        layout.addLayout(self.ram_layout)
        
        # Estado y Progreso
        self.status_label = QLabel("")
        self.progress_bar = QProgressBar()
        self.progress_bar.hide()
        layout.addWidget(self.status_label)
        layout.addWidget(self.progress_bar)
        
        # Botones
        btn_layout = QHBoxLayout()
        self.btn_create = QPushButton("Crear")
        self.btn_cancel = QPushButton("Cancelar")
        self.btn_cancel.setStyleSheet("background-color: #555; color: white;")
        
        self.btn_create.clicked.connect(self.on_create)
        self.btn_cancel.clicked.connect(self.reject)
        
        btn_layout.addWidget(self.btn_cancel)
        btn_layout.addWidget(self.btn_create)
        layout.addLayout(btn_layout)
        
        # Cargar versiones iniciales
        self.on_type_changed(self.type_combo.currentText())

    def on_type_changed(self, s_type):
        """Carga las versiones dinámicamente usando un hilo."""
        self.version_combo.clear()
        self.version_combo.addItem("Cargando...")
        self.version_combo.setEnabled(False)
        self.btn_create.setEnabled(False)
        
        self.fetch_worker = FetchVersionsWorker(s_type)
        self.fetch_worker.finished.connect(self.on_versions_fetched)
        self.fetch_worker.start()

    def on_versions_fetched(self, versions):
        """Actualiza el combobox con las versiones obtenidas."""
        self.version_combo.clear()
        if versions:
            self.version_combo.addItems(versions)
            self.version_combo.setEnabled(True)
            self.btn_create.setEnabled(True)
        else:
            self.version_combo.addItem("Error al cargar")

    def on_create(self):
        """Inicia el proceso de creación del servidor."""
        name = self.name_input.text().strip()
        if not name:
            self.status_label.setText("Error: Ingrese un nombre")
            return
            
        self.btn_create.setEnabled(False)
        self.btn_cancel.setEnabled(False)
        self.name_input.setEnabled(False)
        self.type_combo.setEnabled(False)
        self.version_combo.setEnabled(False)
        self.ram_slider.setEnabled(False)
        
        self.progress_bar.show()
        self.progress_bar.setValue(0)
        
        self.create_worker = CreateServerWorker(
            name,
            self.type_combo.currentText(),
            self.version_combo.currentText(),
            self.ram_slider.value()
        )
        self.create_worker.progress.connect(self.on_progress)
        self.create_worker.finished.connect(self.on_create_finished)
        self.create_worker.start()

    def on_progress(self, val, text):
        self.progress_bar.setValue(val)
        self.status_label.setText(text)

    def on_create_finished(self, success, msg):
        if success:
            self.accept()
        else:
            self.status_label.setText(f"Error: {msg}")
            self.btn_cancel.setEnabled(True)

# ==========================================
# PAGINA DE LISTA DE SERVIDORES
# ==========================================

class ServerListPage(QWidget):
    """Página principal que muestra la lista de servidores."""
    open_server_signal = Signal(dict)

    def __init__(self, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        
        # Título
        title = QLabel("Gestor de Servidores")
        title_font = QFont("Arial", 20, QFont.Bold)
        title.setFont(title_font)
        layout.addWidget(title)
        
        # Área de Scroll para tarjetas
        self.scroll_area = QScrollArea()
        self.scroll_area.setWidgetResizable(True)
        self.scroll_content = QWidget()
        self.scroll_layout = QVBoxLayout(self.scroll_content)
        self.scroll_layout.setAlignment(Qt.AlignTop)
        self.scroll_area.setWidget(self.scroll_content)
        layout.addWidget(self.scroll_area)
        
        # Botón para crear nuevo servidor
        self.btn_new = QPushButton("+")
        self.btn_new.setFont(QFont("Arial", 20, QFont.Bold))
        self.btn_new.setFixedHeight(50)
        self.btn_new.clicked.connect(self.show_create_dialog)
        layout.addWidget(self.btn_new)
        
        self.load_servers()

    def load_servers(self):
        """Carga y muestra la lista de servidores desde core."""
        # Limpiar layout
        for i in reversed(range(self.scroll_layout.count())): 
            widget = self.scroll_layout.itemAt(i).widget()
            if widget is not None:
                widget.setParent(None)
                
        servers = server_core.get_servers()
        for s in servers:
            self.add_server_card(s)

    def add_server_card(self, server_info):
        """Crea y añade una tarjeta para un servidor."""
        card = QFrame()
        card.setObjectName("ServerCard")
        card_layout = QHBoxLayout(card)
        
        # Indicador de estado (ficticio, asumimos apagado)
        status_dot = QLabel("⬤")
        status_dot.setStyleSheet("color: gray; font-size: 16px;")
        
        # Info del servidor
        info_layout = QVBoxLayout()
        name_lbl = QLabel(server_info.get("name", "Unknown"))
        name_lbl.setFont(QFont("Arial", 14, QFont.Bold))
        desc_lbl = QLabel(f"{server_info.get('type', '')} {server_info.get('version', '')} - RAM: {server_info.get('ram', 4)}GB")
        desc_lbl.setStyleSheet("color: #AAA;")
        
        info_layout.addWidget(name_lbl)
        info_layout.addWidget(desc_lbl)
        
        # Botón abrir
        btn_open = QPushButton("Abrir")
        btn_open.clicked.connect(lambda: self.open_server_signal.emit(server_info))
        
        card_layout.addWidget(status_dot)
        card_layout.addLayout(info_layout)
        card_layout.addStretch()
        card_layout.addWidget(btn_open)
        
        self.scroll_layout.addWidget(card)

    def show_create_dialog(self):
        dlg = CreateServerDialog(self)
        if dlg.exec() == QDialog.Accepted:
            self.load_servers()

# ==========================================
# PAGINA DE DETALLES DEL SERVIDOR
# ==========================================

class ServerDetailPage(QWidget):
    """Página de detalles, configuración y consola de un servidor."""
    back_signal = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.server_info = None
        self.run_worker = None
        
        layout = QVBoxLayout(self)
        
        # Barra superior
        top_bar = QHBoxLayout()
        self.btn_back = QPushButton("← Servidores")
        self.btn_back.setStyleSheet("background-color: transparent; text-align: left; text-decoration: underline;")
        self.btn_back.clicked.connect(self.go_back)
        
        self.lbl_title = QLabel("Server Name")
        self.lbl_title.setFont(QFont("Arial", 16, QFont.Bold))
        self.lbl_subtitle = QLabel("Type / Version")
        self.lbl_subtitle.setStyleSheet("color: #AAA;")
        
        top_bar.addWidget(self.btn_back)
        top_bar.addWidget(self.lbl_title)
        top_bar.addWidget(self.lbl_subtitle)
        top_bar.addStretch()
        layout.addLayout(top_bar)
        
        # Cuerpo principal dividido en Config y Consola
        main_body = QHBoxLayout()
        
        # ---- PANEL IZQUIERDO: CONFIGURACIÓN ----
        config_panel = QWidget()
        config_layout = QVBoxLayout(config_panel)
        config_layout.setContentsMargins(0,0,0,0)
        
        form = QFormLayout()
        self.input_port = QLineEdit("25565")
        self.input_players = QSpinBox()
        self.input_players.setRange(1, 1000)
        self.input_players.setValue(20)
        self.input_motd = QLineEdit("Un servidor de Minecraft")
        self.combo_diff = QComboBox()
        self.combo_diff.addItems(["peaceful", "easy", "normal", "hard"])
        self.combo_mode = QComboBox()
        self.combo_mode.addItems(["survival", "creative", "adventure", "spectator"])
        self.chk_pvp = QCheckBox("PVP Habilitado")
        self.chk_pvp.setChecked(True)
        self.chk_online = QCheckBox("Online Mode (Premium)")
        self.chk_online.setChecked(False)
        self.input_seed = QLineEdit()
        
        form.addRow("Puerto:", self.input_port)
        form.addRow("Jugadores:", self.input_players)
        form.addRow("MOTD:", self.input_motd)
        form.addRow("Dificultad:", self.combo_diff)
        form.addRow("Modo de Juego:", self.combo_mode)
        form.addRow(self.chk_pvp)
        form.addRow(self.chk_online)
        form.addRow("Semilla:", self.input_seed)
        
        config_layout.addLayout(form)
        
        # RAM Slider
        self.ram_layout = QHBoxLayout()
        self.ram_slider = QSlider(Qt.Horizontal)
        self.ram_slider.setRange(1, 16)
        self.ram_label = QLabel("4 GB")
        self.ram_slider.valueChanged.connect(lambda v: self.ram_label.setText(f"{v} GB"))
        self.ram_layout.addWidget(QLabel("RAM:"))
        self.ram_layout.addWidget(self.ram_slider)
        self.ram_layout.addWidget(self.ram_label)
        config_layout.addLayout(self.ram_layout)
        
        config_layout.addStretch()
        
        self.btn_save = QPushButton("Guardar Configuración")
        self.btn_open_folder = QPushButton("Abrir Carpeta")
        self.btn_delete = QPushButton("Eliminar Servidor")
        self.btn_delete.setStyleSheet("background-color: #E74C3C; color: white;")
        
        self.btn_save.clicked.connect(self.save_config)
        self.btn_delete.clicked.connect(self.delete_server)
        self.btn_open_folder.clicked.connect(self.open_folder)
        
        config_layout.addWidget(self.btn_save)
        config_layout.addWidget(self.btn_open_folder)
        config_layout.addWidget(self.btn_delete)
        
        config_panel.setFixedWidth(300)
        main_body.addWidget(config_panel)
        
        # ---- PANEL DERECHO: CONSOLA Y CONTROLES ----
        console_panel = QWidget()
        console_layout = QVBoxLayout(console_panel)
        console_layout.setContentsMargins(10,0,0,0)
        
        self.btn_toggle = QPushButton("Iniciar")
        self.btn_toggle.clicked.connect(self.toggle_server)
        console_layout.addWidget(self.btn_toggle)
        
        self.console_output = QTextEdit()
        self.console_output.setReadOnly(True)
        self.console_output.setStyleSheet("background-color: #111; color: #FFFFFF; font-family: monospace;")
        console_layout.addWidget(self.console_output)
        
        cmd_layout = QHBoxLayout()
        self.input_cmd = QLineEdit()
        self.input_cmd.setPlaceholderText("Comando para el servidor...")
        self.input_cmd.setEnabled(False)
        self.btn_send = QPushButton("Enviar")
        self.btn_send.setEnabled(False)
        self.btn_send.clicked.connect(self.send_command)
        
        shortcut = QShortcut(QKeySequence("Return"), self.input_cmd)
        shortcut.activated.connect(self.send_command)
        
        cmd_layout.addWidget(self.input_cmd)
        cmd_layout.addWidget(self.btn_send)
        console_layout.addLayout(cmd_layout)
        
        main_body.addWidget(console_panel)
        layout.addLayout(main_body)
        
    def load_server(self, info):
        """Carga los datos del servidor para mostrarlos en la página."""
        self.server_info = info
        name = info.get("name", "Unknown")
        self.lbl_title.setText(name)
        self.lbl_subtitle.setText(f"{info.get('type', '')} {info.get('version', '')}")
        # Parsear RAM: puede ser int o string como "4G"
        ram_val = info.get('ram', 4)
        if isinstance(ram_val, str):
            ram_val = int(ram_val.replace('G', '').replace('g', '').strip() or '4')
        self.ram_slider.setValue(ram_val)
        
        # Limpiar consola y resetear estado
        self.console_output.clear()
        self.set_server_running_ui(False)
        
        # Cargar config
        cfg = server_core.get_server_config(name)
        if cfg:
            self.input_port.setText(cfg.get('server-port', '25565'))
            self.input_players.setValue(int(cfg.get('max-players', 20)))
            self.input_motd.setText(cfg.get('motd', 'A Minecraft Server'))
            self.combo_diff.setCurrentText(cfg.get('difficulty', 'normal'))
            self.combo_mode.setCurrentText(cfg.get('gamemode', 'survival'))
            self.chk_pvp.setChecked(cfg.get('pvp', 'true').lower() == 'true')
            self.chk_online.setChecked(cfg.get('online-mode', 'false').lower() == 'true')
            self.input_seed.setText(cfg.get('level-seed', ''))

    def save_config(self):
        """Guarda la configuración actual."""
        if not self.server_info: return
        cfg = {
            'server-port': self.input_port.text(),
            'max-players': str(self.input_players.value()),
            'motd': self.input_motd.text(),
            'difficulty': self.combo_diff.currentText(),
            'gamemode': self.combo_mode.currentText(),
            'pvp': 'true' if self.chk_pvp.isChecked() else 'false',
            'online-mode': 'true' if self.chk_online.isChecked() else 'false',
            'level-seed': self.input_seed.text()
        }
        server_core.save_server_config(self.server_info['name'], cfg)
        self.console_log("[GUI] Configuración guardada.")

    def delete_server(self):
        """Elimina el servidor pidiendo confirmación."""
        if not self.server_info: return
        reply = QMessageBox.question(self, 'Confirmar', '¿Seguro que deseas eliminar este servidor?',
                                     QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
        if reply == QMessageBox.Yes:
            server_core.delete_server(self.server_info['name'])
            self.go_back()

    def toggle_server(self):
        """Inicia o detiene el servidor."""
        if not self.server_info: return
        name = self.server_info['name']
        
        if self.run_worker is not None and self.run_worker.isRunning():
            # Detener
            server_core.stop_server(name)
            self.btn_toggle.setEnabled(False) # Deshabilitar hasta que se detenga
        else:
            # Iniciar
            self.set_server_running_ui(True)
            self.console_log("[GUI] Iniciando servidor...")
            self.run_worker = RunServerWorker(name)
            self.run_worker.output.connect(self.console_log)
            self.run_worker.stopped.connect(self.on_server_stopped)
            self.run_worker.start()

    def on_server_stopped(self):
        """Callback cuando el hilo del servidor termina."""
        self.set_server_running_ui(False)
        self.console_log("[GUI] Servidor detenido.")
        self.btn_toggle.setEnabled(True)

    def send_command(self):
        """Envía un comando al servidor y limpia el input."""
        cmd = self.input_cmd.text().strip()
        if cmd and self.server_info:
            server_core.send_command(self.server_info['name'], cmd)
            self.console_log(f"> {cmd}")
            self.input_cmd.clear()

    def console_log(self, text):
        """Añade texto a la consola y hace auto-scroll."""
        self.console_output.append(text)
        scrollbar = self.console_output.verticalScrollBar()
        scrollbar.setValue(scrollbar.maximum())

    def open_folder(self):
        """Abre la carpeta del servidor en el explorador."""
        if self.server_info:
            server_core.open_server_folder(self.server_info.get('path', os.path.join(server_core.SERVERS_DIR, self.server_info['name'])))

    def set_server_running_ui(self, is_running):
        """Actualiza la UI según si el servidor está corriendo o no."""
        if is_running:
            self.btn_toggle.setText("Detener")
            self.btn_toggle.setStyleSheet("background-color: #E74C3C; color: white;")
            self.input_cmd.setEnabled(True)
            self.btn_send.setEnabled(True)
        else:
            self.btn_toggle.setText("Iniciar")
            self.btn_toggle.setStyleSheet("background-color: #2ECC71; color: white;")
            self.input_cmd.setEnabled(False)
            self.btn_send.setEnabled(False)

    def go_back(self):
        """Regresa a la lista de servidores."""
        # Si el servidor está corriendo, detenerlo o advertir (aquí lo dejamos correr en background para simplicidad o detenemos)
        self.back_signal.emit()

# ==========================================
# GESTOR DE AUTENTICACION
# ==========================================

class AuthManagerPage(QWidget):
    """Página para gestionar la configuración de autenticación y la API."""
    def __init__(self, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        
        title = QLabel("Gestor de Autenticación")
        title.setFont(QFont("Arial", 20, QFont.Bold))
        layout.addWidget(title)
        
        # Formulario
        form = QFormLayout()
        
        self.combo_mode = QComboBox()
        self.combo_mode.addItems(["JSON", "PostgreSQL", "Discord"])
        self.combo_mode.currentTextChanged.connect(self.on_mode_changed)
        form.addRow("Modo de Autenticación:", self.combo_mode)
        
        # Campos Postgres
        self.pg_url = QLineEdit()
        self.pg_url.setPlaceholderText("postgresql://user:pass@localhost/db")
        self.pg_url_label = QLabel("DB URL:")
        form.addRow(self.pg_url_label, self.pg_url)
        
        # Campos Discord
        self.disc_client_id = QLineEdit()
        self.disc_client_secret = QLineEdit()
        self.disc_client_secret.setEchoMode(QLineEdit.Password)
        self.disc_redirect = QLineEdit()
        
        self.lbl_disc_id = QLabel("Client ID:")
        self.lbl_disc_sec = QLabel("Client Secret:")
        self.lbl_disc_red = QLabel("Redirect URI:")
        
        form.addRow(self.lbl_disc_id, self.disc_client_id)
        form.addRow(self.lbl_disc_sec, self.disc_client_secret)
        form.addRow(self.lbl_disc_red, self.disc_redirect)
        
        layout.addLayout(form)
        
        # Boton Guardar
        self.btn_save = QPushButton("Guardar Configuración")
        self.btn_save.clicked.connect(self.save_config)
        layout.addWidget(self.btn_save)
        
        # Divisor
        line = QFrame()
        line.setFrameShape(QFrame.HLine)
        line.setFrameShadow(QFrame.Sunken)
        layout.addWidget(line)
        
        # Controles Servidor
        ctrl_layout = QHBoxLayout()
        self.btn_toggle = QPushButton("Iniciar API de Auth")
        self.btn_toggle.clicked.connect(self.toggle_api)
        ctrl_layout.addWidget(self.btn_toggle)
        ctrl_layout.addStretch()
        layout.addLayout(ctrl_layout)
        
        # Consola
        self.console = QTextEdit()
        self.console.setReadOnly(True)
        self.console.setStyleSheet("background-color: #111; color: #FFFFFF; font-family: monospace;")
        layout.addWidget(self.console)
        
        self.api_process = QProcess(self)
        self.api_process.readyReadStandardOutput.connect(self.read_stdout)
        self.api_process.readyReadStandardError.connect(self.read_stderr)
        self.api_process.finished.connect(self.on_api_finished)
        
        self.load_config()
        self.on_mode_changed(self.combo_mode.currentText())
        
    def on_mode_changed(self, mode):
        self.pg_url.hide()
        self.pg_url_label.hide()
        self.disc_client_id.hide()
        self.lbl_disc_id.hide()
        self.disc_client_secret.hide()
        self.lbl_disc_sec.hide()
        self.disc_redirect.hide()
        self.lbl_disc_red.hide()
        
        if mode == "PostgreSQL":
            self.pg_url.show()
            self.pg_url_label.show()
        elif mode == "Discord":
            self.disc_client_id.show()
            self.lbl_disc_id.show()
            self.disc_client_secret.show()
            self.lbl_disc_sec.show()
            self.disc_redirect.show()
            self.lbl_disc_red.show()
            
    def load_config(self):
        try:
            if os.path.exists("auth/auth_config.json"):
                with open("auth/auth_config.json", "r", encoding="utf-8") as f:
                    data = json.load(f)
                    mode = data.get("mode", "JSON")
                    self.combo_mode.setCurrentText(mode)
                    if mode == "PostgreSQL":
                        self.pg_url.setText(data.get("db_url", ""))
                    elif mode == "Discord":
                        self.disc_client_id.setText(data.get("client_id", ""))
                        self.disc_client_secret.setText(data.get("client_secret", ""))
                        self.disc_redirect.setText(data.get("redirect_uri", ""))
        except Exception as e:
            self.console.append(f"Error cargando config: {str(e)}")
            
    def save_config(self):
        mode = self.combo_mode.currentText()
        data = {"mode": mode}
        if mode == "PostgreSQL":
            data["db_url"] = self.pg_url.text()
        elif mode == "Discord":
            data["client_id"] = self.disc_client_id.text()
            data["client_secret"] = self.disc_client_secret.text()
            data["redirect_uri"] = self.disc_redirect.text()
            
        os.makedirs("auth", exist_ok=True)
        try:
            with open("auth/auth_config.json", "w", encoding="utf-8") as f:
                json.dump(data, f, indent=4)
            self.console.append("[GUI] Configuración de Auth guardada.")
        except Exception as e:
            self.console.append(f"Error guardando config: {str(e)}")
            
    def toggle_api(self):
        if self.api_process.state() == QProcess.Running:
            self.api_process.terminate()
            self.btn_toggle.setEnabled(False)
        else:
            self.console.append("[GUI] Iniciando API de Auth...")
            self.api_process.start("uvicorn", ["auth.api:app", "--host", "0.0.0.0", "--port", "8000"])
            self.btn_toggle.setText("Detener API")
            self.btn_toggle.setStyleSheet("background-color: #E74C3C; color: white;")
            
    def read_stdout(self):
        data = self.api_process.readAllStandardOutput().data().decode("utf-8")
        if data:
            self.console.append(data.strip())
        
    def read_stderr(self):
        data = self.api_process.readAllStandardError().data().decode("utf-8")
        if data:
            self.console.append(data.strip())
        
    def on_api_finished(self):
        self.console.append("[GUI] API de Auth detenida.")
        self.btn_toggle.setText("Iniciar API de Auth")
        self.btn_toggle.setStyleSheet("background-color: #2ECC71; color: white;")
        self.btn_toggle.setEnabled(True)

# ==========================================
# VENTANA PRINCIPAL
# ==========================================

class ServerManagerWindow(QMainWindow):
    """Ventana principal que gestiona el cambio de páginas."""
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Gestor de Servidores Minecraft")
        self.resize(900, 600)
        self.setStyleSheet(STYLE_SHEET)
        
        main_widget = QWidget()
        self.setCentralWidget(main_widget)
        main_layout = QVBoxLayout(main_widget)
        main_layout.setContentsMargins(0, 0, 0, 0)
        
        # Barra de Navegación
        nav_layout = QHBoxLayout()
        nav_layout.setContentsMargins(10, 10, 10, 10)
        self.btn_servers = QPushButton("Servidores")
        self.btn_auth = QPushButton("Autenticación")
        nav_layout.addWidget(self.btn_servers)
        nav_layout.addWidget(self.btn_auth)
        nav_layout.addStretch()
        main_layout.addLayout(nav_layout)
        
        # Contenedor Principal (Tabs)
        self.main_stacked = QStackedWidget()
        main_layout.addWidget(self.main_stacked)
        
        # Flujo de Servidores
        self.server_flow_widget = QWidget()
        server_flow_layout = QVBoxLayout(self.server_flow_widget)
        server_flow_layout.setContentsMargins(0, 0, 0, 0)
        
        self.server_stacked = QStackedWidget()
        server_flow_layout.addWidget(self.server_stacked)
        
        self.list_page = ServerListPage()
        self.detail_page = ServerDetailPage()
        self.server_stacked.addWidget(self.list_page)
        self.server_stacked.addWidget(self.detail_page)
        
        self.main_stacked.addWidget(self.server_flow_widget)
        
        # Página de Auth
        self.auth_page = AuthManagerPage()
        self.main_stacked.addWidget(self.auth_page)
        
        # Conectar señales
        self.list_page.open_server_signal.connect(self.show_server_detail)
        self.detail_page.back_signal.connect(self.show_server_list)
        
        self.btn_servers.clicked.connect(self.show_servers_tab)
        self.btn_auth.clicked.connect(self.show_auth_tab)
        
        # Iniciar en pestaña Servidores
        self.show_servers_tab()
        
    def show_servers_tab(self):
        self.main_stacked.setCurrentWidget(self.server_flow_widget)
        self.btn_servers.setStyleSheet("background-color: #27AE60; color: white;")
        self.btn_auth.setStyleSheet("background-color: #2ECC71; color: white;")

    def show_auth_tab(self):
        self.main_stacked.setCurrentWidget(self.auth_page)
        self.btn_auth.setStyleSheet("background-color: #27AE60; color: white;")
        self.btn_servers.setStyleSheet("background-color: #2ECC71; color: white;")
        
    def show_server_detail(self, server_info):
        self.detail_page.load_server(server_info)
        self.server_stacked.setCurrentWidget(self.detail_page)
        
    def show_server_list(self):
        self.list_page.load_servers()
        self.server_stacked.setCurrentWidget(self.list_page)

if __name__ == '__main__':
    app = QApplication(sys.argv)
    window = ServerManagerWindow()
    window.show()
    sys.exit(app.exec())
