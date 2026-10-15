from app.crud import UserService


class BaseView:
    service_class = UserService
    name = "base"

    def handle(self, session):
        service = self.service_class()
        return service.create(session, None)


class UserView(BaseView):
    def post(self, session):
        self.service_class()
        return self.handle(session)

    @staticmethod
    def build():
        return UserView()


def make():
    return UserView.build()
