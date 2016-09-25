namespace DelegateExamples
{
    //private delegate void PrivateAction();
    public delegate void PublicAction();

    //private delegate void PrivateGenericAction<T>(T value);
    public delegate void PublicGenericAction<T>(T value);
}