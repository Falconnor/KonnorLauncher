import sys, re
import os

path = os.path.join(os.path.dirname(__file__), 'server_ui.py')
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix styles
content = content.replace('QWidget { background-color: #1E1E1E; }', 'QWidget { background-color: #1E1E1E; color: white; }')
if 'QComboBox QAbstractItemView' not in content:
    content = content.replace('QLineEdit, QComboBox, QSpinBox {', 'QComboBox QAbstractItemView { background-color: #2A2A2A; color: white; selection-background-color: #2ECC71; selection-color: white; }\nQLineEdit, QComboBox, QSpinBox {')

# Fix Forge
content = content.replace('["Vanilla", "Paper", "Fabric"]', '["Vanilla", "Paper", "Fabric", "Forge"]')

# Fix console colors
content = content.replace('color: #0F0', 'color: #FFFFFF')

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

with open(path, 'r', encoding='utf-8') as f:
    c = f.read()
    print('Forge in file:', 'Forge' in c)
    print('FFFFFF in file:', '#FFFFFF' in c)
