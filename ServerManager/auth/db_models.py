from sqlalchemy import Column, Integer, String, create_engine
from sqlalchemy.ext.declarative import declarative_base

Base = declarative_base()

class User(Base):
    __tablename__ = 'users'
    
    id = Column(Integer, primary_key=True, index=True)
    # Nombre de usuario, debe ser único
    username = Column(String, unique=True, index=True, nullable=False)
    # Hash de la contraseña, nullable para los usuarios registrados a través de Discord
    password_hash = Column(String, nullable=True)
    # ID de Discord, en caso de que se haya autenticado por ahí
    discord_id = Column(String, unique=True, nullable=True)

def init_db(db_url: str):
    """
    Inicializa la conexión a la base de datos y crea las tablas si no existen.
    """
    engine = create_engine(db_url)
    Base.metadata.create_all(bind=engine)
    return engine
