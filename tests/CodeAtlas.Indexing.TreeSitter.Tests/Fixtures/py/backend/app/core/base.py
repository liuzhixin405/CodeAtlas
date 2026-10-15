import logging

logger = logging.getLogger(__name__)


class BaseService:
    def audit(self, action: str) -> None:
        logger.info(action)


def helper():
    return 1
