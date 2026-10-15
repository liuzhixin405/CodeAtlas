from pydantic import BaseModel


class User(BaseModel):
    def save(self):
        pass


class UserRepository:
    def __init__(self, db):
        self.db = db

    def get(self, user_id) -> "User":
        return User()

    def all(self) -> list[User]:
        return []
