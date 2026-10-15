package api

import (
	"net/http"

	"example.com/shop/cart"
	"gorm.io/gorm"
)

type Server struct {
	db    *gorm.DB
	store *cart.Store
}

func NewServer(db *gorm.DB) *Server {
	return &Server{db: db, store: cart.NewStore()}
}

func (s *Server) Handle(w http.ResponseWriter, r *http.Request) {
	s.db.Find(nil)
	w.WriteHeader(200)
	st := cart.NewStore()
	st.Add("x", 1)
	s.Start()
}

func (s *Server) Start() {}

type apiError struct{}

func (apiError) Error() string { return "api" }

func check() error { return nil }

func (s *Server) Close() {
	err := check()
	_ = err.Error()
}
