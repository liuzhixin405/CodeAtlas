package cart

import "io"

// Item 是购物车条目。
type Item struct {
	Name  string
	Count int
}

type Cart struct {
	Base
	items []Item
}

type Total int

type Reader interface {
	Get(name string) (Item, error)
}

type ReadWriter interface {
	Reader
	io.Writer
}

func (c *Cart) Add(name string, n int) {
	c.items = append(c.items, newItem(name, n))
	c.touch()
}

func (s *Store) Reset() {}

func newItem(name string, n int) Item { return Item{Name: name, Count: n} }