namespace NpcValheim.Npc
{
    /// <summary>
    /// The auction house: players list their own stacks and buy from each other, with the NPC
    /// only taking a cut. He never buys or sells anything himself.
    ///
    /// A separate NPC from the merchant on purpose. The two are different trades that happen to
    /// share plumbing: at the merchant you deal with the house at its posted price, here you
    /// deal with another player at theirs, and the house is not a counterparty at all. Putting
    /// both behind one figure made "who am I actually buying from" a question the player had to
    /// work out from which tab happened to be open.
    ///
    /// Everything else -- money, mail, tax, the wire format -- is inherited unchanged, so the
    /// two stay in step by construction rather than by being kept in step by hand.
    /// </summary>
    public class AuctionNpc : MarketplaceNpc
    {
        public override bool HasShop => false;
        public override bool HasAuction => true;
    }
}
