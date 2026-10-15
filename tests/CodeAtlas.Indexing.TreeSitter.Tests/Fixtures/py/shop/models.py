from django.db import models


class Article(models.Model):
    title = models.CharField()


class Profile(models.Model):
    def follow(self, other):
        pass
