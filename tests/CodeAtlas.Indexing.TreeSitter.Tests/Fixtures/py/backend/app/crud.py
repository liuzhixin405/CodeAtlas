from sqlmodel import Session, select

from app.core.base import BaseService
from app.models import User, UserCreate


class UserService(BaseService):
    def create(self, session: Session, user_in: UserCreate) -> User:
        self.audit("create")
        db_obj = User.model_validate(user_in)
        self._save(session, db_obj)
        return db_obj

    def _save(self, session, obj):
        session.add(obj)
        session.commit()


def create_user(*, session: Session, user_create: UserCreate) -> User:
    statement = select(User).where(User.email)
    session.exec(statement)
    return UserService().create(session, user_create)
