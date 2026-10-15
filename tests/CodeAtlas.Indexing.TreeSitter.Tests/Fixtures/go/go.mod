module example.com/shop

go 1.22

require (
	github.com/lib/pq v1.10.9
	gorm.io/gorm v1.25.0
)

replace example.com/libs/util => ./third/util