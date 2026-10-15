package cart

func (s *Store) Add(name string, n int) {
	var c Cart
	c.Add(name, n)
	s.cart.Add(name, n)
}

type Store struct {
	cart *Cart
}

type Base struct{ updated bool }

func (b *Base) touch() { b.updated = true }

func NewStore() *Store {
	return &Store{cart: &Cart{}}
}