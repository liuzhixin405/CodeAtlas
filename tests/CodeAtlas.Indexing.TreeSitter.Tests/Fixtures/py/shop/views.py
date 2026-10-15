from rest_framework import serializers, viewsets

from models import Article, Profile


class ArticleSerializer(serializers.ModelSerializer):
    def create(self, data):
        return Article.objects.create(**data)


class ArticleViewSet(viewsets.GenericViewSet):
    queryset = Article.objects.all()
    serializer_class = ArticleSerializer

    def create(self, request):
        serializer = self.serializer_class(data=request.data)
        serializer.is_valid(raise_exception=True)
        serializer.save()
        return Article.objects.filter(title="x")


class ProfileView(viewsets.GenericViewSet):
    def post(self, request):
        follower = self.request.user.profile
        follower.follow(None)
