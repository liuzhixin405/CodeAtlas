from ..core import base as core_base
from ..core.base import helper as h
from .. import version


def get_deps():
    core_base.helper()
    h()
    return version()
