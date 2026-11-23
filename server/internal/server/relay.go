package server

type relayBroker struct{}

func newRelayBroker() *relayBroker             { return &relayBroker{} }
func (b *relayBroker) abortTransfer(id string) {}
func (s *Server) relayRoutes()                 {}
