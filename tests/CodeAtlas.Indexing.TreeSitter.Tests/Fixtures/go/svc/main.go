package main

import (
	"example.com/shop/cart"
	"example.com/svc/handler"
)

func main() {
	handler.Serve()
	cart.NewStore()
}