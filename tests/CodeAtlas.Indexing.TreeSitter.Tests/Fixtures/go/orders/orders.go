package orders

import (
	"net/http"

	"example.com/shop/cart"
	"example.com/shop/genproto"
	"gorm.io/gorm"
)

type Repo interface {
	Get(id string) (*cart.Item, error)
}

type service struct {
	db      *gorm.DB
	repo    Repo
	cartSvc hipstershop.CartServiceClient
	store   *cart.Store
	carts   map[string]*cart.Cart
	a, b    *cart.Store
}

func (s *service) Load(id string) {
	s.db.Where("id = ?", id).First(nil)
	s.repo.Get(id)
	c, _ := s.cartSvc.GetCart(id)
	for _, item := range c.GetItems() {
		item.GetProductId()
	}
	for _, ct := range s.carts {
		ct.Add("x", 1)
	}
	cart.NewStore().Add("y", 2)
	s.store.Add("z", 3)
	s.b.Add("b", 4)
	Map[string](nil).Add("w", 5)
	hipstershop.NewCartServiceClient("c")
	defaultDB.Exec("x")
}

func Map[T any](xs []T) *cart.Store { return nil }

func (s *service) Handle(w http.ResponseWriter, r *http.Request) {}

func Health(w http.ResponseWriter, r *http.Request) {}

func Register(mux *http.ServeMux, s *service) {
	mux.HandleFunc("/load", s.Handle)
	http.HandleFunc("/health", Health)
	http.Handle("/x", http.StripPrefix("/x", nil))
	mux.HandleFunc("/code", nil)
	http.Error(nil, "x", http.StatusOK)
	Register(mux, s)
}

func Lookup(srv interface{}) {
	registry["a"].Add("k", 1)
	if t, ok := srv.(interface{ Ping() }); ok {
		t.Ping()
	}
}
