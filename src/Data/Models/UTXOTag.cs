namespace NodeGuard.Data.Models;
 
public class UTXOTag : Entity
{
    public string Key { get; set; }
    
    public string Value { get; set; }
    
    // Outpoint of the UTXO in format "hash-index"
    public string Outpoint { get; set; }
}