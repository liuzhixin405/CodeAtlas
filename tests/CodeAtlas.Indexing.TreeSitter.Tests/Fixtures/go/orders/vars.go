package orders

import (
	"example.com/shop/cart"
	"gorm.io/gorm"
)

var defaultDB *gorm.DB

var (
	count, limit int
)

var registry = make(map[string]*cart.Store)

func (s *service) Ping() {}
