using Horse.Mq.Client.Annotations;
using Horse.Mq.Client.Models;

namespace Test.Bus.Models
{
    [QueueName("ex-queue-2")]
    [RouterName("ex-route-2")]
    public class ExceptionModel2 : ITransportableException
    {
        public void Initialize(ExceptionContext context)
        {
        }
    }
}