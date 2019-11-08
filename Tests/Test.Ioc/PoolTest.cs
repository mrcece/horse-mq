using System;
using System.Threading.Tasks;
using Test.Ioc.Services;
using Twino.Ioc;
using Xunit;

namespace Test.Ioc
{
    public class PoolTest
    {
        [Fact]
        public async Task Transient()
        {
            ServiceContainer services = new ServiceContainer();
            services.AddTransientPool<ISingleService, SingleService>();

            IContainerScope scope = services.CreateScope();

            ISingleService single = await services.Get<ISingleService>(scope);
            single.Foo = "single";

            ISingleService s1 = await services.Get<ISingleService>(scope);
            Assert.NotEqual(single.Foo, s1.Foo);

            ISingleService s2 = await services.Get<ISingleService>();
            Assert.NotEqual(single.Foo, s2.Foo);

            IContainerScope scope2 = services.CreateScope();
            ISingleService s3 = await services.Get<ISingleService>(scope2);
            Assert.NotEqual(single.Foo, s3.Foo);
        }

        [Fact]
        public async Task Scoped()
        {
            ServiceContainer services = new ServiceContainer();
            services.AddScopedPool<ISingleService, SingleService>();

            IContainerScope scope = services.CreateScope();

            ISingleService single = await services.Get<ISingleService>(scope);
            single.Foo = "single";

            ISingleService s1 = await services.Get<ISingleService>(scope);
            Assert.Equal(single.Foo, s1.Foo);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.Get<ISingleService>());

            IContainerScope scope2 = services.CreateScope();
            ISingleService s3 = await services.Get<ISingleService>(scope2);
            Assert.NotEqual(single.Foo, s3.Foo);
        }

        [Fact]
        public async Task LongRunning()
        {
            ServiceContainer services = new ServiceContainer();
            services.AddScopedPool<ISingleService, SingleService>();

            for (int i = 0; i < 50; i++)
            {
                IContainerScope scope1 = services.CreateScope();
                IContainerScope scope2 = services.CreateScope();
                
                ISingleService service1 = await services.Get<ISingleService>(scope1);
                ISingleService service2 = await services.Get<ISingleService>(scope2);
                
                Assert.NotNull(service1);
                Assert.NotNull(service2);
                Assert.NotEqual(service1, service2);
                
                await Task.Delay(10);
                
                scope1.Dispose();
                scope2.Dispose();
            }
        }

        [Fact]
        public async Task WaitLimitAndGet()
        {
            ServiceContainer services = new ServiceContainer();
            services.AddScopedPool<ISingleService, SingleService>();

            throw new NotImplementedException();
        }

        [Fact]
        public async Task WaitLimitAndTimeout()
        {
            ServiceContainer services = new ServiceContainer();
            services.AddScopedPool<ISingleService, SingleService>();

            throw new NotImplementedException();
        }
    }
}