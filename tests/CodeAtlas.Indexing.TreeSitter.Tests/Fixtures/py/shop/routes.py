from fastapi import APIRouter

from deps import SessionDep

router = APIRouter()


@router.get("/")
def read_items(session: SessionDep):
    session.exec(1).all()
