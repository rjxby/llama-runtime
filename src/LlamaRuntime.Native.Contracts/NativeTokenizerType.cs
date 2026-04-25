namespace LlamaRuntime.Native.Contracts;

public enum NativeTokenizerType : int
{
    Unknown = -1,
    None = 0,
    SentencePiece = 1,
    Bpe = 2,
    WordPiece = 3,
    Unigram = 4,
    Rwkv = 5,
    Plamo2 = 6
}
