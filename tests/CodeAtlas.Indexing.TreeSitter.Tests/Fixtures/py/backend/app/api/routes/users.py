from fastapi import APIRouter

from app import crud
from app.models import User, UserCreate as UC
from . import items
import app.core.base as base

router = APIRouter()


@router.post(
    "/",
)
def create_user_route(session, user_in: UC) -> User:
    user = crud.create_user(session=session, user_create=user_in)
    items.list_items(user)
    base.helper()
    helper()
    return user
