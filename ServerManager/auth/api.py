import json
import os
from fastapi import FastAPI, HTTPException, Header
from fastapi.responses import RedirectResponse
from pydantic import BaseModel
from passlib.context import CryptContext
import jwt
import requests
from typing import Optional

# Configuración de contexto para hashear contraseñas usando bcrypt
pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")
app = FastAPI(title="Minecraft Server Manager Auth API")

# Cargar configuración desde auth_config.json
CONFIG_FILE = "auth_config.json"
if not os.path.exists(CONFIG_FILE):
    # Valores por defecto para evitar errores al cargar en caso de que el archivo no exista
    CONFIG = {
        "AUTH_MODE": "JSON",
        "JWT_SECRET": "super_secret_key"
    }
else:
    with open(CONFIG_FILE, "r", encoding="utf-8") as f:
        CONFIG = json.load(f)

# AUTH_MODE puede ser 'JSON', 'POSTGRES' o 'DISCORD'
AUTH_MODE = CONFIG.get("AUTH_MODE", "JSON")
JWT_SECRET = CONFIG.get("JWT_SECRET", "super_secret_key")
JWT_ALGORITHM = "HS256"

# Inicialización dependiendo del modo configurado
if AUTH_MODE == "POSTGRES":
    from db_models import init_db, User
    from sqlalchemy.orm import sessionmaker
    DB_URL = CONFIG.get("DB_URL", "postgresql://user:pass@localhost/db")
    engine = init_db(DB_URL)
    SessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)
elif AUTH_MODE == "JSON" or AUTH_MODE == "DISCORD":
    USERS_FILE = "users.json"
    if not os.path.exists(USERS_FILE):
        with open(USERS_FILE, "w", encoding="utf-8") as f:
            json.dump({}, f)

# Modelo Pydantic para el login y registro
class AuthRequest(BaseModel):
    username: str
    password: str

def get_json_users():
    """Obtiene los usuarios desde el archivo JSON"""
    with open(USERS_FILE, "r", encoding="utf-8") as f:
        return json.load(f)

def save_json_users(users_data):
    """Guarda los usuarios en el archivo JSON"""
    with open(USERS_FILE, "w", encoding="utf-8") as f:
        json.dump(users_data, f, indent=4)

def create_jwt_token(username: str):
    """Genera un token JWT para un usuario válido"""
    payload = {"sub": username}
    return jwt.encode(payload, JWT_SECRET, algorithm=JWT_ALGORITHM)

@app.post("/register")
def register(req: AuthRequest):
    """
    Registra un nuevo usuario. Soporta los modos JSON y POSTGRES.
    En el modo DISCORD, el registro se hace de forma automática durante el callback.
    """
    if AUTH_MODE == "DISCORD":
        raise HTTPException(status_code=400, detail="Use Discord to register")
    
    hashed_pw = pwd_context.hash(req.password)
    
    if AUTH_MODE == "JSON":
        users = get_json_users()
        if req.username in users:
            raise HTTPException(status_code=400, detail="El usuario ya existe")
        users[req.username] = {"password_hash": hashed_pw}
        save_json_users(users)
        return {"msg": "Usuario registrado exitosamente"}
        
    elif AUTH_MODE == "POSTGRES":
        db = SessionLocal()
        user_exists = db.query(User).filter(User.username == req.username).first()
        if user_exists:
            db.close()
            raise HTTPException(status_code=400, detail="El usuario ya existe")
        
        new_user = User(username=req.username, password_hash=hashed_pw)
        db.add(new_user)
        db.commit()
        db.close()
        return {"msg": "Usuario registrado exitosamente"}

@app.post("/login")
def login(req: AuthRequest):
    """
    Inicia sesión validando credenciales y devuelve un token JWT.
    """
    if AUTH_MODE == "DISCORD":
        raise HTTPException(status_code=400, detail="Use Discord to login")
        
    if AUTH_MODE == "JSON":
        users = get_json_users()
        user_data = users.get(req.username)
        if not user_data or not user_data.get("password_hash"):
            raise HTTPException(status_code=401, detail="Credenciales inválidas")
        if not pwd_context.verify(req.password, user_data["password_hash"]):
            raise HTTPException(status_code=401, detail="Credenciales inválidas")
            
    elif AUTH_MODE == "POSTGRES":
        db = SessionLocal()
        user = db.query(User).filter(User.username == req.username).first()
        db.close()
        if not user or not user.password_hash or not pwd_context.verify(req.password, user.password_hash):
            raise HTTPException(status_code=401, detail="Credenciales inválidas")
            
    token = create_jwt_token(req.username)
    return {"access_token": token, "token_type": "bearer"}

@app.get("/auth/discord")
def auth_discord():
    """
    Redirige al cliente al endpoint de autorización OAuth2 de Discord.
    """
    client_id = CONFIG.get("DISCORD_CLIENT_ID")
    redirect_uri = CONFIG.get("DISCORD_REDIRECT_URI")
    if not client_id or not redirect_uri:
        raise HTTPException(status_code=500, detail="Falta configuración de Discord en auth_config.json")
        
    discord_url = (f"https://discord.com/api/oauth2/authorize"
                   f"?client_id={client_id}&redirect_uri={redirect_uri}"
                   f"&response_type=code&scope=identify")
    return RedirectResponse(url=discord_url)

@app.get("/auth/discord/callback")
def auth_discord_callback(code: str):
    """
    Endpoint para recibir el código de Discord, solicitar un token e iniciar la sesión.
    Si el usuario no existe, se registra automáticamente.
    """
    client_id = CONFIG.get("DISCORD_CLIENT_ID")
    client_secret = CONFIG.get("DISCORD_CLIENT_SECRET")
    redirect_uri = CONFIG.get("DISCORD_REDIRECT_URI")
    
    # 1. Obtener access_token desde Discord
    token_url = "https://discord.com/api/oauth2/token"
    data = {
        "client_id": client_id,
        "client_secret": client_secret,
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": redirect_uri
    }
    headers = {"Content-Type": "application/x-www-form-urlencoded"}
    r = requests.post(token_url, data=data, headers=headers)
    if r.status_code != 200:
        raise HTTPException(status_code=400, detail="Error al intercambiar el código con Discord")
        
    access_token = r.json().get("access_token")
    
    # 2. Obtener información de perfil del usuario
    user_url = "https://discord.com/api/users/@me"
    headers_user = {"Authorization": f"Bearer {access_token}"}
    r_user = requests.get(user_url, headers=headers_user)
    if r_user.status_code != 200:
        raise HTTPException(status_code=400, detail="Error al obtener datos del usuario de Discord")
        
    user_data = r_user.json()
    discord_id = user_data.get("id")
    username = user_data.get("username")
    
    # 3. Auto-registro o búsqueda de usuario existente
    if AUTH_MODE == "JSON" or AUTH_MODE == "DISCORD":
        users = get_json_users()
        # Buscamos si existe alguien con este discord_id, sino usamos su username actual
        found = False
        for uname, udata in users.items():
            if udata.get("discord_id") == discord_id:
                username = uname
                found = True
                break
        if not found:
            # Para evitar colisiones de nombre, si el username ya está en uso por otro, lo modificamos
            if username in users:
                username = f"{username}_{discord_id[:4]}"
            users[username] = {"discord_id": discord_id, "password_hash": None}
            save_json_users(users)
            
    elif AUTH_MODE == "POSTGRES":
        db = SessionLocal()
        user = db.query(User).filter(User.discord_id == discord_id).first()
        if not user:
            existing = db.query(User).filter(User.username == username).first()
            if existing:
                username = f"{username}_{discord_id[:4]}"
            new_user = User(username=username, discord_id=discord_id, password_hash=None)
            db.add(new_user)
            db.commit()
        else:
            username = user.username
        db.close()
        
    # 4. Generar el JWT para la sesión en el Server Manager
    token = create_jwt_token(username)
    return {"access_token": token, "token_type": "bearer"}

@app.get("/verify")
def verify_token(authorization: Optional[str] = Header(None)):
    """
    Endpoint para validar el JWT y extraer información. Útil para verificar la sesión en el frontend o middlewares.
    """
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Token no provisto o con formato incorrecto")
        
    token = authorization.split(" ")[1]
    try:
        payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGORITHM])
        username = payload.get("sub")
        if not username:
            raise HTTPException(status_code=401, detail="Token inválido")
        return {"username": username, "status": "active"}
    except jwt.ExpiredSignatureError:
        raise HTTPException(status_code=401, detail="Token expirado")
    except jwt.InvalidTokenError:
        raise HTTPException(status_code=401, detail="Token inválido")

if __name__ == "__main__":
    # Comando de ejemplo para correr la aplicación:
    # uvicorn api:app --host 0.0.0.0 --port 8000
    import uvicorn
    uvicorn.run("api:app", host="0.0.0.0", port=8000, reload=True)
