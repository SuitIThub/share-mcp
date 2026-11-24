package de.suitithub.share.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairUriTest {
    @Test fun pairUriRoundTripAndRejects() {
        val u = PairUri.parse("share://pair?code=n5ead5&url=https%3A%2F%2Fx.example%3A443")
        assertEquals(PairUri("https://x.example:443", "N5EAD5"), u)
        assertEquals(u, PairUri.parse(u.toString()))
        assertEquals(PairUri("https://x/a b&c=1+2", "ABC"), PairUri.parse(PairUri("https://x/a b&c=1+2", "ABC").toString()))
        for (bad in listOf("https://pair?code=A&url=b", "share://other?code=A&url=b", "share://pair?code=A", "share://pair?url=b", "share://pair?code=&url=b", "share://pair", "", "N5EAD5", "share://pair?code=%ZZ&url=b")) {
            assertNull(bad, PairUri.parse(bad))
        }
    }
}
