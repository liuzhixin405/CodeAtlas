package main

import (
	"fmt"
	"net/http"

	"example.com/libs/util"
	"example.com/shop/api"
	c "example.com/shop/cart"
	_ "github.com/lib/pq"
	"gorm.io/gorm"
)

func main() {
	db, _ := gorm.Open(nil)
	srv := api.NewServer(db)
	http.HandleFunc("/", srv.Handle)
	store := c.NewStore()
	store.Add("apple", 2)
	var s2 *api.Server = srv
	s2.Start()
	fmt.Println(util.Pad("x"), setup())
	run(srv)
}

func setup() string { return "ok" }