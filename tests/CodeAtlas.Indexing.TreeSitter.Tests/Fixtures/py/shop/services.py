import httpx
from dataclasses import dataclass

from repos import UserRepository
from stubs import *
import demo_pb2


class UserService:
    def __init__(self, repo: UserRepository, db=None):
        self.repo = repo
        self.client = httpx.Client()
        self.cache: UserRepository = make_repo()
        self.other = UserRepository(db)

    def load(self, user_id):
        self.repo.get(user_id)
        self.client.get("/x")
        self.cache.all()
        self.other.get(user_id).save()


@dataclass
class Settings:
    repo: UserRepository
    name: str = "x"

    def run(self):
        self.repo.all()


def make_repo() -> UserRepository:
    return UserRepository(None)


def handler():
    repo = make_repo()
    repo.get(1)
    typed: "UserRepository" = make_repo()
    typed.all()
    for user in repo.all():
        user.save()
    with UserRepository(None) as r:
        r.get(2)
    helper_from_star()
    not_exported()
    request = demo_pb2.Empty()
    demo_pb2.HelloRequest(name="x")
    found = repo.all()
    found.append(None)
    return request
