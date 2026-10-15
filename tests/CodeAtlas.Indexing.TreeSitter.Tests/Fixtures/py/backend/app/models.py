from sqlmodel import Field, SQLModel


class Meta(type):
    pass


class Base(SQLModel):
    def save(self):
        return self.validate()


class User(Base, table=True):
    email: str = Field()

    def save(self):
        super().save()
        return touch(self)


class Tagged(Base, metaclass=Meta):
    pass


class UserCreate(SQLModel):
    pass


def touch(obj: User) -> "User":
    return obj
