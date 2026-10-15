import grpc
import helpers
from helpers import Runner


def main():
    def inner():
        helpers.run()

    channel = grpc.insecure_channel("localhost:50051")
    inner()
    Runner().start()
    print(channel)


class _Servicer(object):
    def Send(self, request):
        return main()
